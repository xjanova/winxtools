Add-Type -AssemblyName System.Drawing

$pngPath = Join-Path $PSScriptRoot "logo.png"
$icoPath = Join-Path $PSScriptRoot "icon.ico"

# Load PNG
$png = [System.Drawing.Bitmap]::FromFile($pngPath)

# Resize to standard icon sizes and create ICO
$sizes = @(256, 128, 64, 48, 32, 16)
$images = @()

foreach ($size in $sizes) {
    $resized = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($resized)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($png, 0, 0, $size, $size)
    $graphics.Dispose()
    $images += $resized
}

# Create icon from the 256x256 image
$icon256 = $images[0]
$iconHandle = $icon256.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($iconHandle)

# Save as ICO
$fs = New-Object System.IO.FileStream($icoPath, [System.IO.FileMode]::Create)
$icon.Save($fs)
$fs.Close()

# Cleanup
$icon.Dispose()
foreach ($img in $images) { $img.Dispose() }
$png.Dispose()

Write-Host "Icon created at: $icoPath"
