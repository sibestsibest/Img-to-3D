<#
.SYNOPSIS
  One-time setup: checks prerequisites, restores .NET and npm packages, downloads models.

.PARAMETER Gpu
  Also checks what TripoSR needs to run on an NVIDIA GPU (driver, CUDA 13, cuDNN 9) and restores
  the GPU build of ONNX Runtime. Then start with: .\scripts\dev.ps1 -Gpu
#>
[CmdletBinding()]
param([switch]$SkipModels, [switch]$Gpu)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
. (Join-Path $PSScriptRoot 'gpu.ps1')

function Require($cmd, $hint) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) { throw "$cmd not found. $hint" }
}

Require dotnet 'Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10'
Require node   'Install Node.js 20.19+ or 22.12+: winget install OpenJS.NodeJS.LTS'
Require npm    'npm ships with Node.js.'
Require pwsh   'dev.ps1 opens its windows with PowerShell 7: winget install Microsoft.PowerShell'
$sdk = (dotnet --list-sdks | Where-Object { $_ -match '^10\.' })
if (-not $sdk) { throw '.NET 10 SDK is required (dotnet --list-sdks shows none).' }
Write-Host "dotnet: $($sdk[0])"
Write-Host "node:   $(node --version)"

$ram = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
if ($ram -lt 16) { Write-Warning "Only $ram GB RAM detected. CPU inference needs ~8 GB free; 16 GB+ recommended." }

if ($Gpu) {
    $check = Test-CudaSetup
    Write-CudaReport $check
    if (-not $check.Ok) {
        throw "TripoSR cannot use the GPU yet: install what is listed above, open a new terminal and run .\scripts\setup.ps1 -Gpu again. (Without -Gpu everything runs on the CPU.)"
    }
    Write-Host 'CUDA:   CUDA 13 runtime and cuDNN 9 found'
}

Push-Location $root
try {
    dotnet restore ImageTo3D.slnx
    if ($Gpu) { dotnet restore src/inference/ImageTo3D.Inference -p:OrtFlavor=Cuda }
    Push-Location src/frontend
    npm ci
    Pop-Location
    if (-not $SkipModels) { & (Join-Path $PSScriptRoot 'download-models.ps1') }
}
finally {
    Pop-Location
}

Write-Host "`nSetup complete. Start everything with: .\scripts\dev.ps1$(if ($Gpu) { ' -Gpu' })"
