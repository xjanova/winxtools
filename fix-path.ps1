$dotnetPath = "C:\Program Files\dotnet"
$oldPath = [Environment]::GetEnvironmentVariable("PATH", "Machine")

if ($oldPath -notlike "*$dotnetPath*") {
    $newPath = "$dotnetPath;$oldPath"
    [Environment]::SetEnvironmentVariable("PATH", $newPath, "Machine")
    Write-Host "Added dotnet to System PATH"
} else {
    Write-Host "dotnet already in PATH"
}

# Also set MSBuildSDKsPath
[Environment]::SetEnvironmentVariable("MSBuildSDKsPath", "C:\Program Files\dotnet\sdk\10.0.101\Sdks", "Machine")
Write-Host "Set MSBuildSDKsPath"

# Show final PATH
Write-Host "`nUpdated System PATH:"
[Environment]::GetEnvironmentVariable("PATH", "Machine")
