using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace NetX.Core.System.Tweaks;

/// <summary>
/// One registry value a trick changes. <see cref="Restore"/> = null means the
/// Windows default is "value not present", so Restore deletes it.
/// </summary>
public sealed class RegSetting
{
    public RegSetting(RegistryHive hive, string path, string name, RegistryValueKind kind,
        object applied, object? restore, params object[] alsoDefault)
    {
        Hive = hive;
        Path = path;
        Name = name;
        Kind = kind;
        Applied = applied;
        Restore = restore;
        AlsoDefault = alsoDefault;
    }

    public RegistryHive Hive { get; }
    public string Path { get; }
    public string Name { get; }
    public RegistryValueKind Kind { get; }
    public object Applied { get; }
    public object? Restore { get; }

    /// <summary>Other values that behave exactly like the Windows default (e.g. an explicit 1 for "on").</summary>
    public object[] AlsoDefault { get; }

    public string Display => $"{Reg.HiveName(Hive)}\\{Path}\\{(Name.Length == 0 ? "(Default)" : Name)} = {Reg.Format(Applied)}";
}

/// <summary>In-process registry access used by the tricks (no reg.exe).</summary>
public static class Reg
{
    public static RegistryKey OpenBase(RegistryHive hive) =>
        RegistryKey.OpenBaseKey(hive, hive == RegistryHive.CurrentUser ? RegistryView.Default : RegistryView.Registry64);

    public static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => hive.ToString()
    };

    public static string Format(object value) => value switch
    {
        int i => i < 0 ? $"0x{unchecked((uint)i):X}" : i.ToString(),
        string s => $"\"{s}\"",
        _ => value.ToString() ?? ""
    };

    public static object? Read(RegistryHive hive, string path, string name)
    {
        using var key = OpenBase(hive).OpenSubKey(path, writable: false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public static int? ReadDword(RegistryHive hive, string path, string name) =>
        Read(hive, path, name) switch
        {
            int i => i,
            long l => unchecked((int)l),
            string s when int.TryParse(s, out var p) => p,
            _ => null
        };

    public static string? ReadString(RegistryHive hive, string path, string name) =>
        Read(hive, path, name) as string;

    public static bool KeyExists(RegistryHive hive, string path)
    {
        using var key = OpenBase(hive).OpenSubKey(path, writable: false);
        return key != null;
    }

    public static void Write(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
    {
        using var key = OpenBase(hive).CreateSubKey(path, writable: true)
                        ?? throw new IOException($"Cannot open {HiveName(hive)}\\{path}");
        key.SetValue(name, value, kind);
    }

    public static void DeleteValue(RegistryHive hive, string path, string name)
    {
        using var key = OpenBase(hive).OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public static bool ValueEquals(object? current, object expected)
    {
        if (current == null) return false;
        return (current, expected) switch
        {
            (int a, int b) => a == b,
            (long a, long b) => a == b,
            (long a, int b) => a == b,
            (int a, long b) => a == b,
            (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
            (string a, int b) => int.TryParse(a, out var p) && p == b,
            _ => Equals(current, expected)
        };
    }

    private enum Match { Applied, Default, Other }

    private static Match Classify(RegSetting s)
    {
        var current = Read(s.Hive, s.Path, s.Name);
        if (ValueEquals(current, s.Applied)) return Match.Applied;
        if (current == null) return Match.Default;
        if (s.Restore != null && ValueEquals(current, s.Restore)) return Match.Default;
        if (s.AlsoDefault.Any(v => ValueEquals(current, v))) return Match.Default;
        return Match.Other;
    }

    /// <summary>All values ours → Applied, all at Windows default → Default, otherwise Custom.</summary>
    public static TrickStatus Detect(IReadOnlyList<RegSetting> settings)
    {
        var matches = settings.Select(Classify).ToList();
        if (matches.All(m => m == Match.Applied)) return TrickStatus.Applied();
        if (matches.All(m => m == Match.Default)) return TrickStatus.Default();
        return TrickStatus.Custom();
    }

    /// <summary>
    /// Writes every value. If one write fails the ones already written are put
    /// back, so the machine never ends up half-changed. Success is reported only
    /// after reading the values back.
    /// </summary>
    public static TrickResult ApplyAll(IReadOnlyList<RegSetting> settings, RestartScope restart = RestartScope.None)
    {
        var written = new List<(RegSetting Setting, object? Before)>();
        try
        {
            foreach (var s in settings)
            {
                var before = Read(s.Hive, s.Path, s.Name);
                Write(s.Hive, s.Path, s.Name, s.Applied, s.Kind);
                written.Add((s, before));
            }
        }
        catch (Exception ex)
        {
            foreach (var (s, before) in Enumerable.Reverse(written))
            {
                try
                {
                    if (before == null) DeleteValue(s.Hive, s.Path, s.Name);
                    else Write(s.Hive, s.Path, s.Name, before, s.Kind);
                }
                catch (Exception rollbackEx)
                {
                    Debug.WriteLine($"Rollback of {s.Display} failed: {rollbackEx.Message}");
                }
            }
            return TrickResult.Fail(TweakErrors.Friendly(ex));
        }

        return Detect(settings).State == TrickState.Applied
            ? TrickResult.Ok(TweakErrors.AppliedText, restart)
            : TrickResult.Fail(TweakErrors.NotVerified);
    }

    /// <summary>Puts every value back to the Windows default (write it, or delete it).</summary>
    public static TrickResult RestoreAll(IReadOnlyList<RegSetting> settings, RestartScope restart = RestartScope.None)
    {
        var failures = new List<Exception>();
        foreach (var s in settings)
        {
            try
            {
                if (s.Restore == null) DeleteValue(s.Hive, s.Path, s.Name);
                else Write(s.Hive, s.Path, s.Name, s.Restore, s.Kind);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0) return TrickResult.Fail(TweakErrors.Friendly(failures[0]));
        return Detect(settings).State == TrickState.Default
            ? TrickResult.Ok(TweakErrors.RestoredText, restart)
            : TrickResult.Fail(TweakErrors.NotVerified);
    }

    public static string Describe(IEnumerable<RegSetting> settings) =>
        string.Join("\n", settings.Select(s => s.Display));
}

/// <summary>Maps exceptions to short, localized messages (never a raw Exception text).</summary>
public static class TweakErrors
{
    public static readonly LocText AppliedText = new("Done — the change is in place.", "เรียบร้อย — ตั้งค่าแล้ว");
    public static readonly LocText RestoredText = new("Done — Windows default restored.", "เรียบร้อย — คืนค่าเริ่มต้นของ Windows แล้ว");

    public static readonly LocText NotVerified = new(
        "Windows did not keep the new value (it may be managed by your organization or by another program).",
        "Windows ไม่ยอมรับค่าใหม่ (อาจถูกควบคุมโดยองค์กรหรือโปรแกรมอื่น)");

    public static readonly LocText NeedAdmin = new(
        "This needs Administrator rights. Restart WinXTools as Administrator and try again.",
        "ต้องใช้สิทธิ์ผู้ดูแลระบบ (Administrator) กรุณาเปิด WinXTools แบบ Administrator แล้วลองใหม่");

    public static LocText Friendly(Exception ex)
    {
        string code = $" (0x{ex.HResult:X8})";
        return ex switch
        {
            UnauthorizedAccessException or SecurityException => new LocText(
                "Windows denied access to this setting — it may be protected or managed by your organization." + code,
                "Windows ไม่อนุญาตให้แก้ค่านี้ — อาจเป็นค่าที่ถูกป้องกันหรือถูกควบคุมโดยองค์กร" + code),
            Win32Exception w when w.NativeErrorCode == 5 => new LocText(
                "Windows denied access (Access is denied)." + code,
                "Windows ไม่อนุญาตให้ดำเนินการ (Access denied)" + code),
            Win32Exception w when w.NativeErrorCode == 1060 => new LocText(
                "This Windows service doesn't exist on this PC.",
                "ไม่มีบริการ (service) นี้ในเครื่อง"),
            TimeoutException => new LocText(
                "Windows took too long to respond. Please try again.",
                "Windows ตอบสนองช้าเกินไป กรุณาลองใหม่อีกครั้ง"),
            IOException => new LocText(
                "The setting could not be saved (the system reported an I/O error)." + code,
                "บันทึกค่าไม่ได้ (ระบบแจ้งข้อผิดพลาดในการอ่าน/เขียน)" + code),
            _ => new LocText(
                "Something went wrong while changing this setting." + code,
                "เกิดข้อผิดพลาดระหว่างเปลี่ยนการตั้งค่านี้" + code)
        };
    }

    /// <summary>Message for a console tool that did not succeed.</summary>
    public static LocText FromCommand(CommandResult r, string tool)
    {
        if (r.StartFailed)
            return new LocText($"Could not start {tool}.", $"เปิด {tool} ไม่ได้");
        if (r.Cancelled)
            return new LocText("Cancelled.", "ยกเลิกแล้ว");
        if (r.TimedOut)
            return new LocText($"{tool} took too long and was stopped.", $"{tool} ใช้เวลานานเกินไปจึงถูกหยุด");
        return new LocText(
            $"{tool} reported an error (exit code {r.ExitCode}). See the details below.",
            $"{tool} แจ้งข้อผิดพลาด (รหัส {r.ExitCode}) ดูรายละเอียดด้านล่าง");
    }
}
