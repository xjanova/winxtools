using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using NetX.Core.Optimization;

namespace NetX.Core.System.Background;

/// <summary>What kind of program something is, which decides what WinXTools may suggest for it.</summary>
public enum ProgramClass
{
    /// <summary>Part of Windows: never touched.</summary>
    Windows,
    /// <summary>Antivirus and other protection: never touched.</summary>
    Security,
    /// <summary>Helpers that drivers and devices need (graphics, audio, touchpad, mouse…): usually keep.</summary>
    Hardware,
    /// <summary>A background updater: not needed at start-up, the program updates when it runs.</summary>
    Updater,
    /// <summary>An ordinary program.</summary>
    App,
    /// <summary>Not properly signed and running from a folder where malware likes to live.</summary>
    Suspicious
}

/// <summary>Who made a program file and what it is.</summary>
public sealed class ProgramInfo
{
    public string Path { get; init; } = "";
    public string? Publisher { get; init; }
    public string? Description { get; init; }
    public string? Product { get; init; }

    /// <summary>Has a valid Authenticode signature of its own (Windows' catalog-signed files count as Windows by location).</summary>
    public bool SignatureValid { get; init; }
    public bool Signed { get; init; }
    public ProgramClass Class { get; init; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Description) ? Description! : global::System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// Classifies program files: Windows' own (by location and Microsoft's Windows
/// signature), security software (Windows Security Center's list plus known
/// makers), device helpers (known hardware makers), updaters, ordinary programs
/// and suspicious ones (unsigned, in a temp/download/public folder). Results are
/// cached per file version, so a page refresh costs nothing.
/// </summary>
public static class ProgramCatalog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Stamp, ProgramInfo Info)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string>? _securityFolders;

    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    private static readonly string[] SecurityMakers =
    {
        "Kaspersky", "ESET", "Avast", "AVG Technologies", "Bitdefender", "NortonLifeLock", "Gen Digital", "Symantec", "McAfee",
        "Trend Micro", "Sophos", "Malwarebytes", "Avira", "Panda Security", "F-Secure", "WithSecure", "Webroot", "CrowdStrike",
        "SentinelOne", "Emsisoft", "G DATA", "Comodo", "Cylance", "Quick Heal", "K7 Computing", "Check Point"
    };

    private static readonly string[] HardwareMakers =
    {
        "NVIDIA", "Advanced Micro Devices", "AMD ", "Intel", "Realtek", "Synaptics", "ELAN Microelectronics", "Logitech", "Razer",
        "Corsair", "ASUSTeK", "Micro-Star", "GIGA-BYTE", "Gigabyte", "ASRock", "Dell", "HP Inc", "Hewlett", "Lenovo", "Qualcomm",
        "Broadcom", "Rivet Networks", "Killer", "Dolby", "DTS", "Waves Audio", "Conexant", "Cirrus Logic", "A-Volute", "Nahimic",
        "SteelSeries", "Kingston", "HyperX", "Creative Technology", "Wacom", "Huion", "XP-PEN", "Elgato", "Tobii", "Samsung Electronics",
        "Western Digital", "Seagate", "Acer", "Huawei", "Xiaomi", "MediaTek", "Thrustmaster", "Turtle Beach", "Roccat", "Glorious"
    };

    private static readonly string[] UpdaterWords = { "update", "updater", "upgrade", "autoupdate", "auto-update", "updatesvc" };

    /// <summary>Cached description of a program file (works without administrator rights).</summary>
    public static ProgramInfo Describe(string path)
    {
        long stamp;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return new ProgramInfo { Path = path, Class = ProgramClass.App };
            stamp = file.LastWriteTimeUtc.Ticks ^ file.Length;
        }
        catch
        {
            return new ProgramInfo { Path = path, Class = ProgramClass.App };
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Info;
        }

        var info = Build(path);
        lock (Gate) Cache[path] = (stamp, info);
        return info;
    }

    private static ProgramInfo Build(string path)
    {
        string? description = null, product = null, company = null;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            description = Clean(version.FileDescription);
            product = Clean(version.ProductName);
            company = Clean(version.CompanyName);
        }
        catch
        {
        }

        bool signed = false;
        string? signer = null;
        try
        {
#pragma warning disable SYSLIB0057 // only reads who signed it; validity comes from WinVerifyTrust below
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            signed = true;
            signer = NameFrom(certificate.Subject);
        }
        catch
        {
        }
        bool valid = signed && Trust.Verify(path);

        var publisher = valid ? signer : company;
        return new ProgramInfo
        {
            Path = path,
            Publisher = publisher,
            Description = description,
            Product = product,
            Signed = signed,
            SignatureValid = valid,
            Class = Classify(path, valid ? signer : null, company, description, product)
        };
    }

    private static ProgramClass Classify(string path, string? signer, string? company, string? description, string? product)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (ProcessKiller.IsProtectedProcess(name)) return ProgramClass.Windows;
        if (IsSecurity(path, signer, company, name)) return ProgramClass.Security;

        bool inWindows = path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase);
        bool driverStore = path.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase);
        if (signer is "Microsoft Windows" or "Microsoft Windows Publisher") return ProgramClass.Windows;
        if (inWindows && !driverStore && !path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)) return ProgramClass.Windows;

        // An updater is an updater even when a hardware maker wrote it (AsusUpdate, LGHUB updater).
        var text = $"{name} {description} {product}";
        if (UpdaterWords.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase))) return ProgramClass.Updater;

        var maker = signer ?? company ?? "";
        if (driverStore || HardwareMakers.Any(m => maker.Contains(m, StringComparison.OrdinalIgnoreCase))) return ProgramClass.Hardware;

        if (signer == null && IsRiskyFolder(path)) return ProgramClass.Suspicious;
        return ProgramClass.App;
    }

    private static bool IsSecurity(string path, string? signer, string? company, string name)
    {
        if (name is "MsMpEng" or "NisSrv" or "MpDefenderCoreService" or "SecurityHealthService" or "SecurityHealthSystray" or "smartscreen" or "MpCmdRun")
            return true;
        var maker = signer ?? company ?? "";
        if (SecurityMakers.Any(m => maker.Contains(m, StringComparison.OrdinalIgnoreCase))) return true;
        var folders = SecurityFolders();
        return folders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Folders of the antivirus products Windows Security Center knows about.</summary>
    private static HashSet<string> SecurityFolders()
    {
        if (_securityFolders != null) return _securityFolders;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT pathToSignedProductExe, pathToSignedReportingExe FROM AntiVirusProduct");
            using var results = searcher.Get();
            foreach (ManagementObject product in results)
            {
                using (product)
                {
                    foreach (var field in new[] { "pathToSignedProductExe", "pathToSignedReportingExe" })
                    {
                        var exe = Environment.ExpandEnvironmentVariables(product[field]?.ToString() ?? "");
                        var dir = exe.Length > 3 ? Path.GetDirectoryName(exe) : null;
                        if (!string.IsNullOrEmpty(dir) && !dir.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase))
                            folders.Add(dir.TrimEnd('\\') + "\\");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProgramCatalog] Security Center: {ex.Message}");
        }
        return _securityFolders = folders;
    }

    /// <summary>Temp, Downloads, Public, the root of ProgramData or of Roaming: where unsigned malware usually sits.</summary>
    private static bool IsRiskyFolder(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        string[] risky =
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments) is { Length: > 0 } pub ? Path.GetDirectoryName(pub) ?? "" : ""
        };
        if (risky.Any(r => r.Length > 3 && dir.StartsWith(r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return true;
        // Directly in ProgramData or AppData\Roaming (no maker folder in between).
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) })
            if (root.Length > 3 && string.Equals(dir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? NameFrom(string subject)
    {
        // "CN=Google LLC, O=Google LLC, L=Mountain View, …" → the organisation, else the common name.
        string? Field(string key)
        {
            foreach (var part in SplitDn(subject))
                if (part.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return part[key.Length..].Trim().Trim('"');
            return null;
        }
        return Clean(Field("O=") ?? Field("CN="));
    }

    private static IEnumerable<string> SplitDn(string dn)
    {
        int start = 0;
        bool quoted = false;
        for (int i = 0; i < dn.Length; i++)
        {
            if (dn[i] == '"') quoted = !quoted;
            else if (dn[i] == ',' && !quoted)
            {
                yield return dn[start..i].Trim();
                start = i + 1;
            }
        }
        yield return dn[start..].Trim();
    }

    private static string? Clean(string? text)
    {
        var value = text?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Authenticode check of a file's own signature (no network: revocation isn't fetched).</summary>
    private static class Trust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool Verify(string path)
        {
            var file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = Marshal.StringToHGlobalUni(path)
            };
            var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            try
            {
                Marshal.StructureToPtr(file, filePtr, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = 2,              // WTD_UI_NONE
                    fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
                    dwUnionChoice = 1,           // WTD_CHOICE_FILE
                    pFile = filePtr,
                    dwStateAction = 1,           // WTD_STATEACTION_VERIFY
                    dwProvFlags = 0x00001000 | 0x00000010 // cache-only URL retrieval, no revocation check
                };
                var action = GenericVerifyV2;
                int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                data.dwStateAction = 2;          // WTD_STATEACTION_CLOSE
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                return result == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(filePtr);
                Marshal.FreeHGlobal(file.pcwszFilePath);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
    }
}
