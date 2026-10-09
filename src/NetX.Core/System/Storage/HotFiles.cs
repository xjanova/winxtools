using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetX.Core.System.Storage;

/// <summary>A program Windows recorded starting, as far as one drive is concerned.</summary>
public sealed class LaunchedProgram
{
    /// <summary>The program file as Windows recorded it ("GAME.EXE").</summary>
    public string Name { get; init; } = "";

    /// <summary>Recorded launches, each halved for every week since it happened.</summary>
    public double Score { get; init; }

    public int LaunchesThisWeek { get; init; }
    public DateTime LastLaunchUtc { get; init; }

    /// <summary>Files it loads from this drive while starting.</summary>
    public int FileCount { get; init; }

    /// <summary>The program's own file, when it is on this drive ("\GAMES\X\GAME.EXE").</summary>
    public string? ExecutablePath { get; init; }
}

/// <summary>A file programs load from the drive (path from the volume root, "\GAMES\X\DATA.PAK").</summary>
public sealed record HotFile(string Path, string Program, double Score);

public sealed class HotFileReport
{
    /// <summary>False when Windows' Prefetch folder couldn't be read (not administrator, or Prefetch is off).</summary>
    public bool PrefetchReadable { get; init; }

    /// <summary>Programs Windows has records of (on any drive).</summary>
    public int ProgramsRecorded { get; init; }

    /// <summary>Programs that load files from this drive, most used first.</summary>
    public List<LaunchedProgram> Programs { get; init; } = new();

    /// <summary>Their files on this drive, in the order to place them: most used program first, each in the order it loads them.</summary>
    public List<HotFile> Files { get; init; } = new();
}

/// <summary>
/// Which files a drive's programs load when they start, read from what Windows
/// already records: every time a program starts, Windows' Prefetch notes the
/// files it loads in its first seconds and when it ran (the last 8 times) — the
/// same records Windows uses to lay out the boot files on the system drive.
/// Reading them costs nothing while you work: nothing is watched or recorded by
/// WinXTools itself. Limit: files a program opens later (a game's next level)
/// aren't in these records. Needs administrator rights.
/// </summary>
public static unsafe class HotFiles
{
    private const long MaxPrefetchFileBytes = 8L << 20;
    private const ushort CompressionXpressHuff = 4;
    private static readonly double HalfLifeTicks = TimeSpan.FromDays(7).Ticks;
    private static readonly TimeSpan Stale = TimeSpan.FromDays(60);

    public static HotFileReport Read(char letter, CancellationToken token = default)
    {
        letter = char.ToUpperInvariant(letter);
        uint? serial = GetVolumeInformationW($@"{letter}:\", null, 0, out uint number, out _, out _, null, 0) ? number : null;
        var device = new StringBuilder(512);
        string? devicePath = QueryDosDeviceW($"{letter}:", device, device.Capacity) > 0 ? device.ToString() : null;

        string[] records;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            records = Directory.GetFiles(folder, "*.pf");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HotFiles] Prefetch unreadable: {ex.Message}");
            return new HotFileReport { PrefetchReadable = false };
        }

        var now = DateTime.UtcNow;
        var programs = new List<(LaunchedProgram Program, List<string> Files)>();
        int recorded = 0;
        var workspace = new Workspace();
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            var parsed = PrefetchRecord.Read(record, workspace);
            if (parsed == null) continue;
            recorded++;

            var runs = parsed.RunsUtc.Where(t => t <= now.AddDays(1)).ToList();
            if (runs.Count == 0 || now - runs.Max() > Stale) continue;

            var files = new List<string>();
            foreach (var name in parsed.Files)
                if (OnVolume(name, serial, devicePath) is { } relative && !IsExcluded(relative)) files.Add(relative);

            // Only programs installed on this drive: a tool that lives elsewhere (git, python…)
            // lists whatever data it happened to read there, which isn't what gets used again.
            var executable = files.FirstOrDefault(f => IsProgramFile(f, parsed.Program));
            if (executable == null) continue;

            programs.Add((new LaunchedProgram
            {
                Name = parsed.Program,
                // Regular use beats bursts: one point per day it ran (of its last 8 runs), halved per week of age.
                Score = runs.Select(t => t.Date).Distinct().Sum(day => Math.Pow(0.5, Math.Max(0, (now - day).Ticks) / HalfLifeTicks)),
                LaunchesThisWeek = runs.Count(t => now - t <= TimeSpan.FromDays(7)),
                LastLaunchUtc = runs.Max(),
                FileCount = files.Count,
                ExecutablePath = executable
            }, files));
        }

        var ordered = programs.OrderByDescending(p => p.Program.Score).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hot = new List<HotFile>();
        foreach (var (program, files) in ordered)
            foreach (var file in files)
                if (seen.Add(file)) hot.Add(new HotFile(file, program.Name, program.Score));

        return new HotFileReport
        {
            PrefetchReadable = true,
            ProgramsRecorded = recorded,
            Programs = ordered.Select(p => p.Program).ToList(),
            Files = hot
        };
    }

    /// <summary>
    /// "\VOLUME{01d5…-a1b2c3d4}\GAMES\X.PAK" (Windows 8+: creation time and serial number)
    /// or "\DEVICE\HARDDISKVOLUME7\GAMES\X.PAK" → "\GAMES\X.PAK" when it is on this volume.
    /// </summary>
    private static string? OnVolume(string name, uint? serial, string? devicePath)
    {
        if (name.StartsWith(@"\VOLUME{", StringComparison.OrdinalIgnoreCase))
        {
            int close = name.IndexOf('}');
            int dash = close > 0 ? name.LastIndexOf('-', close) : -1;
            if (serial == null || dash < 0 || close + 1 >= name.Length || name[close + 1] != '\\') return null;
            return uint.TryParse(name.AsSpan(dash + 1, close - dash - 1), global::System.Globalization.NumberStyles.HexNumber, null, out uint s) &&
                   s == serial
                ? name[(close + 1)..]
                : null;
        }
        if (devicePath != null && name.Length > devicePath.Length + 1 && name[devicePath.Length] == '\\' &&
            name.StartsWith(devicePath, StringComparison.OrdinalIgnoreCase))
            return name[devicePath.Length..];
        return null;
    }

    /// <summary>The record keeps only the first 29 characters of a long program name.</summary>
    private static bool IsProgramFile(string path, string program)
    {
        var name = Path.GetFileName(path);
        return program.Length >= 29
            ? name.StartsWith(program, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".EXE", StringComparison.OrdinalIgnoreCase)
            : name.Equals(program, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>File-system metadata and the page/hibernation files never move.</summary>
    private static bool IsExcluded(string path) =>
        path.Length < 2 || path[1] == '$' || path.EndsWith('\\') ||
        path.StartsWith(@"\System Volume Information", StringComparison.OrdinalIgnoreCase) ||
        path.Equals(@"\pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
        path.Equals(@"\hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
        path.Equals(@"\swapfile.sys", StringComparison.OrdinalIgnoreCase);

    #region Prefetch records (.pf)

    private sealed record PrefetchData(string Program, List<DateTime> RunsUtc, List<string> Files);

    /// <summary>Scratch memory for decompression, reused for every record.</summary>
    private sealed class Workspace
    {
        public byte[]? Buffer;
    }

    private static class PrefetchRecord
    {
        /// <summary>Program name, last run times and the files it loads; null for anything unexpected.</summary>
        public static PrefetchData? Read(string path, Workspace workspace)
        {
            byte[] raw;
            try
            {
                var info = new FileInfo(path);
                if (info.Length < 84 || info.Length > MaxPrefetchFileBytes) return null;
                raw = File.ReadAllBytes(path);
            }
            catch
            {
                return null;
            }

            // Windows 10/11 store them compressed: "MAM" + algorithm (+ 0x80 when a checksum follows).
            var data = raw[0] == 'M' && raw[1] == 'A' && raw[2] == 'M' ? Decompress(raw, workspace) : raw;
            if (data == null || data.Length < 224 || data[4] != 'S' || data[5] != 'C' || data[6] != 'C' || data[7] != 'A')
                return null;
            int version = BitConverter.ToInt32(data, 0);
            if (version is not (26 or 30 or 31)) return null; // Windows 8.1, 10, 11

            // Header: program name @16 (60 bytes UTF-16). File information: filename strings offset @100, size @104,
            // last 8 run times (FILETIME) @128.
            string program = Utf16Z(data, 16, 60);
            int stringsOffset = BitConverter.ToInt32(data, 100);
            int stringsSize = BitConverter.ToInt32(data, 104);
            if (stringsOffset < 84 || stringsSize <= 0 || (long)stringsOffset + stringsSize > data.Length) return null;

            var files = new List<string>();
            int pos = stringsOffset, end = stringsOffset + stringsSize;
            while (pos + 1 < end)
            {
                int start = pos;
                while (pos + 1 < end && (data[pos] | data[pos + 1]) != 0) pos += 2;
                if (pos > start && pos - start < 64_000) files.Add(Encoding.Unicode.GetString(data, start, pos - start));
                pos += 2;
            }

            var runs = new List<DateTime>();
            for (int i = 0; i < 8; i++)
            {
                long fileTime = BitConverter.ToInt64(data, 128 + i * 8);
                if (fileTime <= 0) continue;
                try
                {
                    var t = DateTime.FromFileTimeUtc(fileTime);
                    if (t.Year >= 2012) runs.Add(t);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }
            return program.Length == 0 ? null : new PrefetchData(program, runs, files);
        }

        private static byte[]? Decompress(byte[] raw, Workspace workspace)
        {
            if (raw.Length < 12) return null;
            byte flags = raw[3];
            ushort format = (ushort)(flags & 0x0F);
            int dataOffset = (flags & 0x80) != 0 ? 12 : 8;
            uint size = BitConverter.ToUInt32(raw, 4);
            if (format != CompressionXpressHuff || size < 84 || size > 4 * MaxPrefetchFileBytes) return null;

            if (workspace.Buffer == null)
            {
                if (RtlGetCompressionWorkSpaceSize(format, out uint bufferSpace, out uint fragmentSpace) != 0) return null;
                workspace.Buffer = new byte[Math.Max(bufferSpace, fragmentSpace)];
            }

            var output = new byte[size];
            fixed (byte* o = output)
            fixed (byte* c = raw)
            fixed (byte* w = workspace.Buffer)
            {
                uint status = RtlDecompressBufferEx(format, o, size, c + dataOffset, (uint)(raw.Length - dataOffset), out uint final, w);
                if (status != 0 || final < 84) return null;
                if (final < size) Array.Resize(ref output, (int)final);
            }
            return output;
        }

        private static string Utf16Z(byte[] data, int offset, int maxBytes)
        {
            int end = offset;
            while (end + 1 < offset + maxBytes && (data[end] | data[end + 1]) != 0) end += 2;
            return Encoding.Unicode.GetString(data, offset, end - offset).Trim();
        }
    }

    #endregion

    #region Native

    [DllImport("ntdll.dll")]
    private static extern uint RtlGetCompressionWorkSpaceSize(ushort format, out uint bufferWorkSpaceSize, out uint fragmentWorkSpaceSize);

    [DllImport("ntdll.dll")]
    private static extern uint RtlDecompressBufferEx(ushort format, byte* uncompressed, uint uncompressedSize, byte* compressed,
        uint compressedSize, out uint finalUncompressedSize, byte* workspace);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder? name, int nameSize, out uint serial,
        out uint maxComponent, out uint flags, StringBuilder? fileSystem, int fileSystemSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string deviceName, StringBuilder targetPath, int max);

    #endregion
}
