// Signs a published WinXTools folder so the in-app updater will install it.
//
//   dotnet run --project tools/ReleaseSigner -c Release -- <publish folder> <version>
//
// The private key (base64 PKCS#8, ECDSA P-256) comes from the WINXTOOLS_UPDATE_SIGNING_KEY
// environment variable — in CI, the repository secret of the same name. Writes
// update-manifest.json (every file's path, size and SHA-256) and update-manifest.sig into
// the folder, then proves the result with the app's own verifier against the public key
// compiled into WinXTools. A key that does not match that public key fails the release
// instead of publishing an update nobody could install.

using System.Security.Cryptography;
using System.Text.Json;
using NetX.Core.System;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: ReleaseSigner <publish folder> <version>");
    return 2;
}

var folder = Path.GetFullPath(args[0]);
var version = args[1].Trim().TrimStart('v', 'V');
if (!Directory.Exists(folder))
{
    Console.Error.WriteLine($"Folder not found: {folder}");
    return 2;
}

var secret = Environment.GetEnvironmentVariable("WINXTOOLS_UPDATE_SIGNING_KEY");
if (string.IsNullOrWhiteSpace(secret))
{
    Console.Error.WriteLine("WINXTOOLS_UPDATE_SIGNING_KEY is not set. Refusing to publish an update that WinXTools cannot verify.");
    return 1;
}

var manifestPath = Path.Combine(folder, UpdatePackageVerifier.ManifestFileName);
var signaturePath = Path.Combine(folder, UpdatePackageVerifier.SignatureFileName);
File.Delete(manifestPath);
File.Delete(signaturePath);

var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
    .OrderBy(path => path, StringComparer.Ordinal)
    .Select(path =>
    {
        using var stream = File.OpenRead(path);
        return new
        {
            path = Path.GetRelativePath(folder, path).Replace('\\', '/'),
            size = new FileInfo(path).Length,
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()
        };
    })
    .ToList();

if (!files.Any(f => f.path.Equals("WinXTools.exe", StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine("WinXTools.exe is not in the publish folder.");
    return 1;
}

var manifest = JsonSerializer.SerializeToUtf8Bytes(
    new { product = "winx-tools", version, files },
    new JsonSerializerOptions { WriteIndented = true });

byte[] signature;
using (var key = ECDsa.Create())
{
    try
    {
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(secret.Trim()), out _);
    }
    catch (Exception ex) when (ex is FormatException or CryptographicException)
    {
        Console.Error.WriteLine("WINXTOOLS_UPDATE_SIGNING_KEY is not a base64 PKCS#8 ECDSA key.");
        return 1;
    }
    signature = key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}

File.WriteAllBytes(manifestPath, manifest);
File.WriteAllText(signaturePath, Convert.ToBase64String(signature));

// Check the result exactly as the app will, on a copy (the verifier removes the manifest).
var check = Path.Combine(Path.GetTempPath(), "winxtools-sign-check-" + Guid.NewGuid().ToString("N"));
try
{
    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(check, Path.GetRelativePath(folder, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }

    var verdict = UpdatePackageVerifier.Verify(check, version, "0.0.0");
    if (verdict != UpdatePackageVerifier.Result.Valid)
    {
        Console.Error.WriteLine($"The signed folder does not verify with the public key built into WinXTools ({verdict}). " +
                                "Is the secret the private half of UpdatePackageVerifier.ReleasePublicKey?");
        return 1;
    }
}
finally
{
    try { Directory.Delete(check, recursive: true); } catch { }
}

Console.WriteLine($"Signed {files.Count} files for WinXTools {version}.");
return 0;
