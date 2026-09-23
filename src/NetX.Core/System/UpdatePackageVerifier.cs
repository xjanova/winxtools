using System.Security.Cryptography;
using System.Text.Json;

namespace NetX.Core.System;

/// <summary>
/// Proves an unpacked update came from the WinXTools release pipeline.
///
/// WinXTools runs as Administrator and installs its own updates, so whoever can change the
/// download can run code as admin on every PC that updates. HTTPS and the server's SHA-256
/// only prove the file arrived as the server sent it; they cannot help if the server (or the
/// GitHub release) was tampered with. So every release zip carries update-manifest.json —
/// the version and the size + SHA-256 of every file — and update-manifest.sig, an ECDSA
/// P-256 signature over the manifest made in CI with a key that never leaves GitHub secrets.
/// Nothing is installed unless the signature checks out against the public key below, the
/// version is the one announced and newer than this one, and the folder holds exactly the
/// files the manifest lists, byte for byte.
/// </summary>
public static class UpdatePackageVerifier
{
    public const string ManifestFileName = "update-manifest.json";
    public const string SignatureFileName = "update-manifest.sig";
    private const string ProductSlug = "winx-tools";

    // SubjectPublicKeyInfo of the release signing key (ECDSA P-256), created 2026-09-23.
    // The private half is the WINXTOOLS_UPDATE_SIGNING_KEY secret of github.com/xjanova/winxtools.
    private const string ReleasePublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEB9xV89rnfEp5trdEMrZfRZ2I90Pwuh/EzF4+6L/Yf7erAl6xe1Y2p9U3LopIXl0aedGEQekGtYCqXiPaoBXNmQ==";

    private const long MaxManifestBytes = 1024 * 1024;

    public enum Result
    {
        Valid,
        /// <summary>No manifest or signature in the package (e.g. a bare exe or an older release).</summary>
        Unsigned,
        BadSignature,
        /// <summary>Signed, but for another product or version than the one announced, or not newer.</summary>
        WrongVersion,
        /// <summary>A listed file is missing or differs, or an unlisted file is present.</summary>
        FilesMismatch
    }

    /// <summary>
    /// Checks <paramref name="packageDir"/> (the unpacked update). On <see cref="Result.Valid"/>
    /// the manifest and signature files are removed so they are not copied into the install folder.
    /// </summary>
    public static Result Verify(string packageDir, string expectedVersion, string currentVersion) =>
        Verify(packageDir, expectedVersion, currentVersion, Convert.FromBase64String(ReleasePublicKey));

    /// <summary>Same, against another public key (tests).</summary>
    public static Result Verify(string packageDir, string expectedVersion, string currentVersion, byte[] publicKeySpki)
    {
        var manifestPath = Path.Combine(packageDir, ManifestFileName);
        var signaturePath = Path.Combine(packageDir, SignatureFileName);
        if (!File.Exists(manifestPath) || !File.Exists(signaturePath)) return Result.Unsigned;

        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Length > MaxManifestBytes || new FileInfo(signaturePath).Length > 1024) return Result.BadSignature;

        var manifestBytes = File.ReadAllBytes(manifestPath);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(File.ReadAllText(signaturePath).Trim());
        }
        catch (FormatException)
        {
            return Result.BadSignature;
        }

        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            if (!key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return Result.BadSignature;
        }

        // Only now is the content trusted enough to parse.
        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return Result.FilesMismatch;
        }
        if (manifest?.Files == null || manifest.Files.Count == 0) return Result.FilesMismatch;

        if (!string.Equals(manifest.Product, ProductSlug, StringComparison.OrdinalIgnoreCase)
            || !SameVersion(manifest.Version, expectedVersion)
            || !IsNewer(manifest.Version, currentVersion))
            return Result.WrongVersion;

        var root = Path.GetFullPath(packageDir);
        var rootWithSlash = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || entry.Sha256 is not { Length: 64 }) return Result.FilesMismatch;

            var relative = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Contains(':')) return Result.FilesMismatch;

            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase)) return Result.FilesMismatch;
            if (!listed.Add(full)) return Result.FilesMismatch;

            var file = new FileInfo(full);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length != entry.Size)
                return Result.FilesMismatch;

            using var stream = file.OpenRead();
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                return Result.FilesMismatch;
        }

        // Anything the release did not sign (a planted DLL next to the exe, say) stops the install.
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Result.FilesMismatch;
            if (info.Attributes.HasFlag(FileAttributes.Directory)) continue;

            var name = Path.GetFileName(path);
            bool isSignatureFile = Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && (name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) || name.Equals(SignatureFileName, StringComparison.OrdinalIgnoreCase));
            if (!isSignatureFile && !listed.Contains(Path.GetFullPath(path))) return Result.FilesMismatch;
        }

        File.Delete(manifestPath);
        File.Delete(signaturePath);
        return Result.Valid;
    }

    private static bool SameVersion(string? a, string? b) =>
        string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    private static string Trim(string? version) => (version ?? "").Trim().TrimStart('v', 'V');

    /// <summary>
    /// True when <paramref name="candidate"/> is a newer release than <paramref name="current"/>
    /// ("1.2.0" &gt; "1.1.9"; a final release is newer than its own beta).
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!AutoUpdateService.TryParseVersion(candidate, out var next, out var nextIsPre)) return false;
        if (!AutoUpdateService.TryParseVersion(current, out var now, out var nowIsPre)) return true;
        return next > now || (next == now && nowIsPre && !nextIsPre);
    }

    private sealed class Manifest
    {
        public string? Product { get; set; }
        public string? Version { get; set; }
        public List<ManifestFile>? Files { get; set; }
    }

    private sealed class ManifestFile
    {
        public string? Path { get; set; }
        public long Size { get; set; }
        public string? Sha256 { get; set; }
    }
}
