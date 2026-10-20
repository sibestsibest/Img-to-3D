<#
.SYNOPSIS
  Sets up a Stability AI engine (SF3D or SPAR3D) for the Python inference service on Windows
  (GPU when available, else CPU).

.DESCRIPTION
  1. Clones Stability-AI/stable-fast-3d or Stability-AI/stable-point-aware-3d (pinned commit) into third_party/.
  2. Creates src/inference-sf3d/.venv (SF3D) or .venv-spar3d (SPAR3D) with Python 3.11 via uv. The
     engines need separate venvs: their texture_baker/uv_unwrapper extensions share package names.
  3. Installs pinned dependencies (PyTorch CPU), then, on a machine with an NVIDIA GPU and the
     CUDA 12.x toolkit (nvcc), swaps in the CUDA build of PyTorch.
  4. Builds the texture_baker and uv_unwrapper C++ extensions (needs MSVC: Visual Studio
     Build Tools 2022 with the "Desktop development with C++" workload), with CUDA kernels in GPU mode.
  5. Downloads Real-ESRGAN x4plus (used to sharpen small photos before projection).

  Model weights are gated: accept the license at https://huggingface.co/stabilityai/stable-fast-3d
  (and https://huggingface.co/stabilityai/stable-point-aware-3d for SPAR3D) and provide a read token
  via the HF_TOKEN environment variable (or `uvx hf auth login`).

.PARAMETER Engine
  SF3D (default) or SPAR3D (Stable Point-Aware 3D: better hidden sides, slower).

.PARAMETER Device
  Auto (default): GPU if nvidia-smi and nvcc are found, otherwise CPU. Cpu / Cuda force a mode.
  Re-run with a different value to switch; the extensions are rebuilt for the chosen mode.
#>
[CmdletBinding()]
param(
    [ValidateSet('SF3D', 'SPAR3D')][string]$Engine = 'SF3D',
    [switch]$SkipExtensions,
    [ValidateSet('Auto', 'Cpu', 'Cuda')][string]$Device = 'Auto'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$service = Join-Path $root 'src/inference-sf3d'
if ($Engine -eq 'SPAR3D') {
    $repo = Join-Path $root 'third_party/stable-point-aware-3d'
    $repoUrl = 'https://github.com/Stability-AI/stable-point-aware-3d.git'
    $package = 'spar3d'
    $commit = 'fdc311b16809e6a8adc2f5a3407ebb3db1a95bd1'
    $venv = '.venv-spar3d'
    $requirements = 'requirements-spar3d.txt'
    $hfRepo = 'stable-point-aware-3d'
}
else {
    $repo = Join-Path $root 'third_party/stable-fast-3d'
    $repoUrl = 'https://github.com/Stability-AI/stable-fast-3d.git'
    $package = 'sf3d'
    $commit = 'ff21fc491b4dc5314bf6734c7c0dabd86b5f5bb2'
    $venv = '.venv'
    $requirements = 'requirements.txt'
    $hfRepo = 'stable-fast-3d'
}

$uv = (Get-Command uv -ErrorAction SilentlyContinue)?.Source
if (-not $uv) { $uv = Join-Path $env:USERPROFILE '.local\bin\uv.exe' }
if (-not (Test-Path $uv)) { throw 'uv not found. Install it: powershell -c "irm https://astral.sh/uv/install.ps1 | iex"' }

# The SF3D extensions only get CUDA kernels when compiled with nvcc; without them a CUDA build of
# PyTorch cannot run SF3D's texture baking, so GPU mode needs the driver and the toolkit.
$hasGpu = [bool](Get-Command nvidia-smi -ErrorAction SilentlyContinue)
$nvcc = (Get-Command nvcc -ErrorAction SilentlyContinue)?.Source
if (-not $nvcc -and $env:CUDA_PATH -and (Test-Path "$env:CUDA_PATH\bin\nvcc.exe")) { $nvcc = "$env:CUDA_PATH\bin\nvcc.exe" }
$useCuda = switch ($Device) {
    'Cuda' {
        if (-not $hasGpu) { throw 'Device=Cuda but nvidia-smi was not found (no NVIDIA driver).' }
        if (-not $nvcc) { throw 'Device=Cuda needs the CUDA 12.x toolkit (nvcc) to build the SF3D extensions. Install it from https://developer.nvidia.com/cuda-downloads.' }
        $true
    }
    'Cpu' { $false }
    default {
        if ($hasGpu -and -not $nvcc) { Write-Warning 'NVIDIA GPU found but no CUDA toolkit (nvcc); installing the CPU build. Install CUDA 12.x and re-run to use the GPU.' }
        $hasGpu -and [bool]$nvcc
    }
}
Write-Host ("$Engine device mode: " + ($(if ($useCuda) { 'CUDA' } else { 'CPU' })))

if (-not (Test-Path (Join-Path $repo $package))) {
    New-Item -ItemType Directory -Force (Split-Path $repo) | Out-Null
    git clone $repoUrl $repo
}
git -C $repo fetch --depth 1 origin $commit 2>$null
git -C $repo checkout --quiet $commit

Push-Location $service
try {
    if (-not (Test-Path "$venv\Scripts\python.exe")) {
        & $uv python install 3.11
        & $uv venv $venv --python 3.11
    }
    & $uv pip install --python "$venv\Scripts\python.exe" -r $requirements
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
    if ($useCuda) {
        & $uv pip install --python "$venv\Scripts\python.exe" --reinstall-package torch --reinstall-package torchvision `
            --index-url https://download.pytorch.org/whl/cu124 torch==2.5.1+cu124 torchvision==0.20.1+cu124
        if ($LASTEXITCODE -ne 0) { throw 'Installing the CUDA build of PyTorch failed.' }
    }

    if (-not $SkipExtensions) {
        $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
        $vs = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath } else { $null }
        if (-not $vs) {
            throw @'
MSVC (C++ build tools) not found. Install Visual Studio Build Tools 2022 with the C++ workload, e.g. in an
elevated PowerShell:
  curl.exe -L -o vs_BuildTools.exe https://aka.ms/vs/17/release/vs_BuildTools.exe
  .\vs_BuildTools.exe --passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended
Then re-run this script.
'@
        }

        # Build inside a VS developer environment so torch.utils.cpp_extension finds cl.exe.
        $devShell = Join-Path $vs 'Common7\Tools\Launch-VsDevShell.ps1'
        $py = (Resolve-Path "$venv\Scripts\python.exe").Path
        $build = @"
`$env:PATH = '${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;' + `$env:PATH
& '$devShell' -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
# Some VS installs (seen with VS 2026) do not detect an installed Windows SDK; add it explicitly.
if (-not `$env:WindowsSdkDir) {
    `$kits = '${env:ProgramFiles(x86)}\Windows Kits\10'
    `$ver = (Get-ChildItem "`$kits\Include" -Directory | Where-Object { Test-Path "`$(`$_.FullName)\ucrt\float.h" } | Sort-Object Name -Descending | Select-Object -First 1).Name
    if (-not `$ver) { throw 'Windows 10/11 SDK not found. Add it in the Visual Studio Installer.' }
    `$env:INCLUDE += ";`$kits\Include\`$ver\ucrt;`$kits\Include\`$ver\um;`$kits\Include\`$ver\shared;`$kits\Include\`$ver\winrt"
    `$env:LIB += ";`$kits\Lib\`$ver\ucrt\x64;`$kits\Lib\`$ver\um\x64"
    `$env:PATH = "`$kits\bin\`$ver\x64;" + `$env:PATH
}
`$env:DISTUTILS_USE_SDK = '1'
`$env:USE_CUDA = '$(if ($useCuda) { '1' } else { '0' })'
`$env:USE_NATIVE_ARCH = '0'
& '$uv' pip install --python '$py' --no-build-isolation --reinstall-package texture_baker --reinstall-package uv_unwrapper '$repo\texture_baker' '$repo\uv_unwrapper'
exit `$LASTEXITCODE
"@
        pwsh -NoProfile -Command $build
        if ($LASTEXITCODE -ne 0) { throw "Building the $Engine C++ extensions failed." }
    }

    & "$venv\Scripts\python.exe" -c "import torch, texture_baker, uv_unwrapper; print('$Engine extensions OK; CUDA available:', torch.cuda.is_available())"
}
finally {
    Pop-Location
}

# Real-ESRGAN x4plus (BSD-3-Clause) sharpens small photos before they are projected onto the texture.
$esrgan = Join-Path $root 'models/realesrgan/RealESRGAN_x4plus.pth'
$esrganSha = '4FA0D38905F75AC06EB49A7951B426670021BE3018265FD191D2125DF9D682F1'
if (-not (Test-Path $esrgan) -or (Get-FileHash $esrgan -Algorithm SHA256).Hash -ne $esrganSha) {
    New-Item -ItemType Directory -Force (Split-Path $esrgan) | Out-Null
    curl.exe -fL -o "$esrgan.download" https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth
    if ($LASTEXITCODE -ne 0) { throw 'Downloading Real-ESRGAN weights failed.' }
    if ((Get-FileHash "$esrgan.download" -Algorithm SHA256).Hash -ne $esrganSha) { throw 'Real-ESRGAN weights failed the SHA-256 check.' }
    Move-Item -Force "$esrgan.download" $esrgan
}

if (-not $env:HF_TOKEN) {
    Write-Warning "HF_TOKEN is not set. Accept the license at https://huggingface.co/stabilityai/$hfRepo and set a read token before the first run."
}
Write-Host "`nStart the service with: .\scripts\run-sf3d.ps1 -Engine $Engine"
