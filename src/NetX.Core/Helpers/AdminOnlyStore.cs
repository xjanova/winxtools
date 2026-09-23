using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace NetX.Core.Helpers;

/// <summary>
/// Admin-only home for state files the elevated app later ACTS on (saved
/// service originals, kill rules, auto-optimize settings). %LocalAppData% is
/// writable by any non-elevated process of the user, so a file planted there
/// could make the elevated app kill processes or reconfigure services.
///
/// %ProgramData%\WinXTools is created owned by Administrators with a protected
/// DACL (Administrators + SYSTEM only, nothing inherited from ProgramData);
/// every file is written with the same explicit DACL. On load both the folder
/// and the file must still be admin-owned and writable by nobody else, and the
/// folder must not be a junction/symlink. Callers still validate the content
/// against their own whitelist.
/// </summary>
public static class AdminOnlyStore
{
    private const long MaxFileBytes = 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstallerSid =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    // Any of these granted to someone else means the file could have been altered.
    private const FileSystemRights WriteLikeRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    private static string? _verifiedDirectory;
    private static bool? _isElevated;

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinXTools");

    public static bool Exists(string fileName)
    {
        try { return File.Exists(Path.Combine(DirectoryPath, fileName)); }
        catch { return false; }
    }

    /// <summary>Null when the file is missing, not trustworthy or unreadable.</summary>
    public static T? Load<T>(string fileName) where T : class
    {
        lock (Gate)
        {
            try
            {
                var directory = GetSecureDirectory(create: false);
                if (directory == null)
                    return null;

                var file = new FileInfo(Path.Combine(directory, fileName));
                if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > MaxFileBytes)
                    return null;

                if (!IsAdminOnly(file.GetAccessControl()))
                {
                    Debug.WriteLine($"[AdminOnlyStore] Ignoring {fileName}: not admin-only.");
                    return null;
                }

                return JsonSerializer.Deserialize<T>(File.ReadAllText(file.FullName));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AdminOnlyStore] Load {fileName} failed: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Atomically writes the file with an admin-only DACL. False when unavailable.</summary>
    public static bool Save<T>(string fileName, T value)
    {
        lock (Gate)
        {
            try
            {
                var directory = GetSecureDirectory(create: true);
                if (directory == null)
                    return false;

                var target = Path.Combine(directory, fileName);
                var temp = new FileInfo(target + ".tmp");
                if (temp.Exists)
                    temp.Delete();

                var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
                using (var stream = temp.Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData,
                           FileShare.None, 4096, FileOptions.WriteThrough, BuildSecurity<FileSecurity>(inheritable: false)))
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                File.Move(temp.FullName, target, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AdminOnlyStore] Save {fileName} failed: {ex.Message}");
                return false;
            }
        }
    }

    private static string? GetSecureDirectory(bool create)
    {
        if (_verifiedDirectory != null)
            return _verifiedDirectory;
        if (!IsElevated())
            return null; // can't create or verify an admin-only folder without admin

        var info = new DirectoryInfo(DirectoryPath);
        if (!info.Exists)
        {
            if (!create)
                return null;
            info.Create(BuildSecurity<DirectorySecurity>(inheritable: true));
            info.Refresh();
        }

        // Never follow a junction/symlink someone planted in ProgramData — the ACL
        // fix below would otherwise be applied to whatever it points at.
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return null;

        var security = info.GetAccessControl();
        if (!security.AreAccessRulesProtected || !IsAdminOnly(security))
        {
            // Pre-created by someone else or loosened later: take it back. Files
            // already inside keep their own ACL/owner and are checked on load.
            info.SetAccessControl(BuildSecurity<DirectorySecurity>(inheritable: true));
            if (!IsAdminOnly(info.GetAccessControl()))
                return null;
        }

        _verifiedDirectory = info.FullName;
        return _verifiedDirectory;
    }

    private static TSecurity BuildSecurity<TSecurity>(bool inheritable) where TSecurity : FileSystemSecurity, new()
    {
        var security = new TSecurity();
        security.SetOwner(AdministratorsSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var inheritance = inheritable
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        foreach (var sid in new[] { AdministratorsSid, LocalSystemSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    private static bool IsAdminOnly(FileSystemSecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
            return false;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            if (rule.IdentityReference is SecurityIdentifier sid && IsTrusted(sid))
                continue;

            var rights = (int)rule.FileSystemRights;
            if ((rule.FileSystemRights & WriteLikeRights) != 0 || (rights & (GenericAll | GenericWrite)) != 0)
                return false;
        }

        return true;
    }

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid == AdministratorsSid || sid == LocalSystemSid || sid == TrustedInstallerSid;

    private static bool IsElevated()
    {
        if (_isElevated is bool known)
            return known;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            _isElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            _isElevated = false;
        }

        return _isElevated.Value;
    }
}
