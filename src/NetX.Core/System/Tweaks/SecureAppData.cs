using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace NetX.Core.System.Tweaks;

/// <summary>
/// Admin-only storage in %ProgramData%\WinXTools.
///
/// Backups that are later written back into HKLM by the elevated app must not
/// live anywhere a normal (non-elevated) process can edit them — otherwise any
/// user-level program could plant values the app then writes as Administrator.
/// The folder is owned by Administrators, its ACL grants only Administrators
/// and SYSTEM (inheritance disabled), and a folder pre-created by someone else
/// (or a junction) is moved aside instead of trusted.
/// </summary>
public static class SecureAppData
{
    private const long MaxStateFileBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _root;
    private static string? _scratch;

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static string RootPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinXTools");

    /// <summary>Returns the secured folder, creating or repairing it. Throws when it can't be secured (not elevated).</summary>
    public static string GetDirectory()
    {
        lock (Gate)
        {
            if (_root != null && Directory.Exists(_root)) return _root;
            _root = EnsureSecureDirectory(RootPath);
            return _root;
        }
    }

    /// <summary>Admin-only temp folder for elevated child processes (DISM scratch files etc.).</summary>
    public static string? TryGetScratchDirectory()
    {
        try
        {
            lock (Gate)
            {
                if (_scratch != null && Directory.Exists(_scratch)) return _scratch;
                _scratch = EnsureSecureDirectory(Path.Combine(GetDirectory(), "temp"));
                return _scratch;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SecureAppData: no scratch folder: {ex.Message}");
            return null;
        }
    }

    /// <summary>Reads a JSON state file; returns null when missing or unreadable.</summary>
    public static T? Load<T>(string fileName) where T : class
    {
        try
        {
            var path = Path.Combine(GetDirectory(), fileName);
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaxStateFileBytes)
                return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SecureAppData: cannot read {fileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes a JSON state file atomically (temp file + rename), so a crash
    /// mid-write never leaves a half-written backup. Throws on failure — callers
    /// must not change the system when the backup could not be saved.
    /// </summary>
    public static void Save<T>(string fileName, T value)
    {
        var dir = GetDirectory();
        var path = Path.Combine(dir, fileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string fileName)
    {
        try
        {
            var path = Path.Combine(GetDirectory(), fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SecureAppData: cannot delete {fileName}: {ex.Message}");
        }
    }

    private static DirectorySecurity BuildSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { Administrators, LocalSystem })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }
        security.SetOwner(Administrators);
        return security;
    }

    private static string EnsureSecureDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // Junction/symlink planted by someone else: remove the link itself (not its target).
                Directory.Delete(path);
                info.Refresh();
            }
            else if (!HasTrustedOwner(info))
            {
                // Pre-created by a non-admin: don't reuse anything inside it.
                Directory.Move(path, path + ".untrusted-" + DateTime.UtcNow.Ticks);
                info.Refresh();
            }
        }

        if (!info.Exists)
            BuildSecurity().CreateDirectory(path);

        // Re-apply every time in case the ACL was loosened.
        new DirectoryInfo(path).SetAccessControl(BuildSecurity());
        return path;
    }

    private static bool HasTrustedOwner(DirectoryInfo info)
    {
        try
        {
            var owner = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner != null && (owner == Administrators || owner == LocalSystem || owner == TrustedInstaller);
        }
        catch
        {
            return false;
        }
    }
}
