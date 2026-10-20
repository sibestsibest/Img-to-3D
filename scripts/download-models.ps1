<#
.SYNOPSIS
  Downloads the ONNX models (~3.5 GB) and sample images, verifying SHA-256 checksums.

.DESCRIPTION
  - TripoSR (stabilityai/TripoSR, MIT) exported to ONNX by the community repo brodatech/triposr-onnx
    (export of VAST-AI-Research/TripoSR commit d26e331, opset 17).
  - U^2-Net (u2net.onnx), the default background-removal model used by rembg.
  Files that already exist with the right checksum are skipped.
#>
[CmdletBinding()]
param(
    [string]$ModelDir = (Join-Path $PSScriptRoot '..\models'),
    [string]$SampleDir = (Join-Path $PSScriptRoot '..\samples')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is very slow with the progress bar

$triposr = 'https://huggingface.co/brodatech/triposr-onnx/resolve/main'
$files = @(
    @{ Url = "$triposr/triplane.onnx";      Path = 'triposr/triplane.onnx';      Sha = '70740d13d9c240df1f97e55c08d588fe3c930c5adbab64c2bea3d023fb07494f' },
    @{ Url = "$triposr/triplane.onnx_data"; Path = 'triposr/triplane.onnx_data'; Sha = '3c9725a9279127ea7828a4aa025eb58fac9c841cd77f9d5241367613b3286cae' },
    @{ Url = "$triposr/nerf.onnx";          Path = 'triposr/nerf.onnx';          Sha = '3800e60cff68c45d6a299154d5daa50831ca3204ee245407da500fc2a01fbe7b' },
    @{ Url = "$triposr/config.yaml";        Path = 'triposr/config.yaml';        Sha = '74ca708ce086bf68e97709ea6b3d91f14717921c04691e84043f0eb8fcc68e62' },
    @{ Url = "$triposr/LICENSE.txt";        Path = 'triposr/LICENSE.txt';        Sha = 'b5ed400bf58c5d7ebc253530c96cfb9a3506261809ed1d4438c22d23d5f6fc41' },
    @{ Url = 'https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx'; Path = 'u2net/u2net.onnx'; Sha = '8d10d2f3bb75ae3b6d527c77944fc5e7dcd94b29809d47a739a7a728a912b491' }
)

foreach ($f in $files) {
    $target = Join-Path $ModelDir $f.Path
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    if ((Test-Path $target) -and ((Get-FileHash $target -Algorithm SHA256).Hash.ToLower() -eq $f.Sha)) {
        Write-Host "OK (cached)  $($f.Path)"
        continue
    }
    Write-Host "Downloading  $($f.Path) ..."
    $temp = "$target.download"
    # curl.exe ships with Windows 10+ and handles multi-GB files and redirects well.
    & curl.exe -fL --retry 3 -o $temp $f.Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed: $($f.Url)" }
    $hash = (Get-FileHash $temp -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $f.Sha) {
        Remove-Item $temp -Force
        throw "Checksum mismatch for $($f.Path): expected $($f.Sha), got $hash. The upstream file may have changed; verify it before updating the checksum."
    }
    Move-Item $temp $target -Force
    Write-Host "OK           $($f.Path)"
}

New-Item -ItemType Directory -Force $SampleDir | Out-Null
foreach ($name in 'chair.png', 'robot.png') {
    $target = Join-Path $SampleDir $name
    if (-not (Test-Path $target)) {
        & curl.exe -fsSL -o $target "https://raw.githubusercontent.com/VAST-AI-Research/TripoSR/main/examples/$name"
        if ($LASTEXITCODE -ne 0) { throw "Sample download failed: $name" }
    }
}

Write-Host "`nModels are in $((Resolve-Path $ModelDir).Path)"
