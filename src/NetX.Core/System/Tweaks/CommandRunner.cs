using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetX.Core.System.Tweaks;

/// <summary>
/// Thread-safe text sink for console output. It understands carriage-return
/// progress lines (DISM/chkdsk redraw "[==== 45% ====]" with '\r'), so the
/// dialog shows the latest progress instead of hundreds of partial lines.
/// </summary>
public sealed class ConsoleOutputBuffer
{
    private const int MaxChars = 2_000_000;

    private readonly object _lock = new();
    private readonly StringBuilder _done = new();
    private readonly StringBuilder _current = new();
    private bool _pendingCr;
    private int _version;

    /// <summary>Changes every time text is appended (cheap change detection for UI polling).</summary>
    public int Version
    {
        get { lock (_lock) return _version; }
    }

    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_lock)
        {
            foreach (var ch in text)
            {
                switch (ch)
                {
                    case '﻿':
                        break; // byte-order mark from UTF-16 tools
                    case '\r':
                        _pendingCr = true;
                        break;
                    case '\n':
                        _done.Append(_current).Append('\n');
                        _current.Clear();
                        _pendingCr = false;
                        break;
                    default:
                        if (_pendingCr)
                        {
                            // A bare '\r' means "rewrite the current line" (progress bars).
                            _current.Clear();
                            _pendingCr = false;
                        }
                        _current.Append(ch);
                        break;
                }
            }

            if (_done.Length > MaxChars)
                _done.Remove(0, _done.Length - MaxChars / 2).Insert(0, "[…]\n");

            _version++;
        }
    }

    public void AppendLine(string? line = null) => Append((line ?? "") + "\n");

    public string GetText()
    {
        lock (_lock)
            return _current.Length == 0 ? _done.ToString() : _done.ToString() + _current;
    }
}

public sealed class CommandResult
{
    public int ExitCode { get; init; } = -1;
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public bool StartFailed { get; init; }

    public bool Succeeded => !TimedOut && !Cancelled && !StartFailed && ExitCode == 0;

    /// <summary>stdout followed by stderr, trimmed.</summary>
    public string Output
    {
        get
        {
            var o = StdOut.Trim();
            var e = StdErr.Trim();
            if (e.Length == 0) return o;
            if (o.Length == 0) return e;
            return o + Environment.NewLine + e;
        }
    }
}

/// <summary>
/// Runs Windows console tools without a shell: no cmd.exe, no window, exit code
/// and output captured, timeout + kill, cancellation.
///
/// Output is decoded with the OEM code page (874 on Thai Windows) — the default
/// UTF-8/ANSI decoding garbles Thai console text — and tools that write UTF-16
/// when redirected (sfc.exe) are detected automatically.
///
/// The app runs elevated, so children get a hardened environment: tools are
/// started by full System32 path, PATH/PSModulePath point only at system
/// folders and TEMP points at an admin-only folder. User-writable locations
/// (HKCU\Environment, %TEMP%, Documents\WindowsPowerShell) can't inject code.
/// </summary>
public static class CommandRunner
{
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static readonly Lazy<Encoding> Oem = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    });

    /// <summary>The console (OEM) code page of this Windows installation.</summary>
    public static Encoding OemEncoding => Oem.Value;

    public static string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Full path of a tool in System32 (never resolved through PATH).</summary>
    public static string SystemTool(string exeName) => Path.Combine(Environment.SystemDirectory, exeName);

    public static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>Quotes one command-line argument (CommandLineToArgvW rules).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <param name="fileName">Full path of the executable.</param>
    /// <param name="arguments">Command-line arguments (already quoted).</param>
    /// <param name="timeout">The process tree is killed when it runs longer.</param>
    /// <param name="live">Optional live output sink for the output dialog.</param>
    /// <param name="outputEncoding">Force an encoding (PowerShell = UTF-8); null = OEM with UTF-16 detection.</param>
    /// <param name="stdin">Text written to standard input before it is closed. Input is always closed so a
    /// tool that unexpectedly asks a question reads EOF instead of hanging.</param>
    public static async Task<CommandResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken ct = default,
        ConsoleOutputBuffer? live = null,
        Encoding? outputEncoding = null,
        string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        HardenEnvironment(psi);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return new CommandResult { StartFailed = true };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CommandRunner: cannot start {fileName}: {ex.Message}");
            return new CommandResult { StartFailed = true };
        }

        try
        {
            if (!string.IsNullOrEmpty(stdin))
                await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch { /* the tool may already have exited */ }

        var outText = new StringBuilder();
        var errText = new StringBuilder();
        var outPump = PumpAsync(process.StandardOutput.BaseStream, outputEncoding, outText, live);
        var errPump = PumpAsync(process.StandardError.BaseStream, outputEncoding, errText, live);

        bool timedOut = false, cancelled = false;
        using (var timeoutCts = new CancellationTokenSource(timeout))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
        {
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = ct.IsCancellationRequested;
                timedOut = !cancelled;
                try { process.Kill(entireProcessTree: true); } catch { }
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch { }
            }
        }

        // The pipes close when the process exits; a grandchild that inherited
        // them could keep them open, so never wait for the readers forever.
        try
        {
            await Task.WhenAll(outPump, errPump).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch { }

        int exitCode = -1;
        try
        {
            if (process.HasExited) exitCode = process.ExitCode;
        }
        catch { }

        string stdout, stderr;
        lock (outText) stdout = outText.ToString();
        lock (errText) stderr = errText.ToString();

        return new CommandResult
        {
            ExitCode = exitCode,
            StdOut = stdout,
            StdErr = stderr,
            TimedOut = timedOut,
            Cancelled = cancelled
        };
    }

    /// <summary>Convenience overload for a System32 tool.</summary>
    public static Task<CommandResult> RunToolAsync(
        string exeName, string arguments, TimeSpan timeout,
        CancellationToken ct = default, ConsoleOutputBuffer? live = null, string? stdin = null) =>
        RunAsync(SystemTool(exeName), arguments, timeout, ct, live, null, stdin);

    /// <summary>
    /// Runs a PowerShell script via -EncodedCommand (no quoting problems, no
    /// profile, non-interactive). Output is UTF-8; any error ends the script
    /// with exit code 1 and the message on stderr.
    /// </summary>
    public static Task<CommandResult> RunPowerShellAsync(
        string script, TimeSpan timeout, CancellationToken ct = default, ConsoleOutputBuffer? live = null)
    {
        var wrapped =
            "$ErrorActionPreference = 'Stop'\n" +
            "$ProgressPreference = 'SilentlyContinue'\n" +
            "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false\n" +
            "try {\n" + script + "\n}\ncatch {\n  [Console]::Error.WriteLine($_.Exception.Message)\n  exit 1\n}\n";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        return RunAsync(PowerShellPath,
            "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            timeout, ct, live, new UTF8Encoding(false));
    }

    /// <summary>Escapes a value for a single-quoted PowerShell string literal.</summary>
    public static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static void HardenEnvironment(ProcessStartInfo psi)
    {
        var sys = Environment.SystemDirectory;
        var win = WindowsDirectory;
        var env = psi.Environment;

        env["SystemRoot"] = win;
        env["windir"] = win;
        env["ComSpec"] = Path.Combine(sys, "cmd.exe");
        env["PATH"] = string.Join(";",
            sys, win, Path.Combine(sys, "Wbem"), Path.Combine(sys, @"WindowsPowerShell\v1.0"));
        env["PSModulePath"] = string.Join(";",
            Path.Combine(sys, @"WindowsPowerShell\v1.0\Modules"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WindowsPowerShell\Modules"));
        env.Remove("PSExecutionPolicyPreference");

        // DISM and others unpack helper DLLs into %TEMP%; keep that out of the
        // user-writable temp folder.
        var scratch = SecureAppData.TryGetScratchDirectory();
        if (scratch != null)
        {
            env["TEMP"] = scratch;
            env["TMP"] = scratch;
        }
    }

    private static async Task PumpAsync(Stream stream, Encoding? forced, StringBuilder sink, ConsoleOutputBuffer? live)
    {
        var buffer = new byte[8192];
        var chars = new char[16384];
        var pending = new MemoryStream();
        Decoder? decoder = forced?.GetDecoder();

        void Emit(byte[] bytes, int offset, int count, bool flush)
        {
            int n = decoder!.GetChars(bytes, offset, count, chars, 0, flush);
            if (n == 0) return;
            var text = new string(chars, 0, n).Replace("﻿", "");
            lock (sink) sink.Append(text);
            live?.Append(text);
        }

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false)) > 0)
            {
                if (decoder == null)
                {
                    pending.Write(buffer, 0, read);
                    if (pending.Length < 16) continue;
                    decoder = Sniff(pending.GetBuffer(), (int)pending.Length).GetDecoder();
                    Emit(pending.GetBuffer(), 0, (int)pending.Length, false);
                    pending.SetLength(0);
                }
                else
                {
                    Emit(buffer, 0, read, false);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CommandRunner: read failed: {ex.Message}");
        }

        try
        {
            if (decoder == null)
            {
                decoder = Sniff(pending.GetBuffer(), (int)pending.Length).GetDecoder();
                Emit(pending.GetBuffer(), 0, (int)pending.Length, true);
            }
            else
            {
                Emit(Array.Empty<byte>(), 0, 0, true);
            }
        }
        catch { }
    }

    /// <summary>
    /// Console tools write the OEM code page, except a few (sfc.exe) that write
    /// UTF-16LE when redirected. OEM text never contains NUL bytes, UTF-16 text
    /// has them in the high byte of every ASCII character (spaces, digits, CR/LF).
    /// </summary>
    private static Encoding Sniff(byte[] bytes, int length)
    {
        if (length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;

        int pairs = length / 2, zeroHigh = 0;
        for (int i = 1; i < length; i += 2)
            if (bytes[i] == 0) zeroHigh++;

        if (pairs > 0 && zeroHigh >= 2 && zeroHigh * 10 >= pairs) return Encoding.Unicode;
        return OemEncoding;
    }
}
