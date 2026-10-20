<#
.SYNOPSIS
  Sets up the Stable Fast 3D (SF3D) Python inference service on Windows (CPU).

.DESCRIPTION
  1. Clones Stability-AI/stable-fast-3d (pinned commit) into third_party/.
  2. Creates src/inference-sf3d/.venv with Python 3.11 via uv.
  3. Installs pinned dependencies (PyTorch CPU) from requirements.txt.
  4. Builds the texture_baker and uv_unwrapper C++ extensions (needs MSVC: Visual Studio
     Build Tools 2022 with the "Desktop development with C++" workload).

  Model weights are gated: accept the license at https://huggingface.co/stabilityai/stable-fast-3d
  and provide a read token via the HF_TOKEN environment variable (or `uvx hf auth login`).
#>
[CmdletBinding()]
param([switch]$SkipExtensions)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$service = Join-Path $root 'src/inference-sf3d'
$repo = Join-Path $root 'third_party/stable-fast-3d'
$commit = 'ff21fc491b4dc5314bf6734c7c0dabd86b5f5bb2'

$uv = (Get-Command uv -ErrorAction SilentlyContinue)?.Source
if (-not $uv) { $uv = Join-Path $env:USERPROFILE '.local\bin\uv.exe' }
if (-not (Test-Path $uv)) { throw 'uv not found. Install it: powershell -c "irm https://astral.sh/uv/install.ps1 | iex"' }

if (-not (Test-Path (Join-Path $repo 'sf3d'))) {
    New-Item -ItemType Directory -Force (Split-Path $repo) | Out-Null
    git clone https://github.com/Stability-AI/stable-fast-3d.git $repo
}
git -C $repo fetch --depth 1 origin $commit 2>$null
git -C $repo checkout --quiet $commit

Push-Location $service
try {
    if (-not (Test-Path '.venv\Scripts\python.exe')) {
        & $uv python install 3.11
        & $uv venv .venv --python 3.11
    }
    & $uv pip install --python .venv\Scripts\python.exe -r requirements.txt
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }

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
        $py = (Resolve-Path '.venv\Scripts\python.exe').Path
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
`$env:USE_CUDA = '0'
`$env:USE_NATIVE_ARCH = '0'
& '$uv' pip install --python '$py' --no-build-isolation '$repo\texture_baker' '$repo\uv_unwrapper'
exit `$LASTEXITCODE
"@
        pwsh -NoProfile -Command $build
        if ($LASTEXITCODE -ne 0) { throw 'Building the SF3D C++ extensions failed.' }
    }

    & .venv\Scripts\python.exe -c "import texture_baker, uv_unwrapper; print('SF3D extensions OK')"
}
finally {
    Pop-Location
}

if (-not $env:HF_TOKEN) {
    Write-Warning 'HF_TOKEN is not set. Accept the license at https://huggingface.co/stabilityai/stable-fast-3d and set a read token before the first run.'
}
Write-Host "`nStart the service with: .\scripts\run-sf3d.ps1"
