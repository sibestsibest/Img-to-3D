<#
.SYNOPSIS
  Starts an inference engine, the API and the Vite dev server in separate windows.
  Open http://localhost:5173 when all three are up.

.PARAMETER Engine
  TripoSR (default): .NET + ONNX Runtime service on :5090.
  SF3D: Stable Fast 3D Python service on :5091 (run scripts/setup-sf3d.ps1 first; needs HF_TOKEN).
  SPAR3D: Stable Point-Aware 3D on :5092, better hidden sides, slower (setup-sf3d.ps1 -Engine SPAR3D).

.PARAMETER Gpu
  TripoSR only: run on the NVIDIA GPU (CUDA 13 + cuDNN 9; check with .\scripts\setup.ps1 -Gpu).
  Refuses to start when the GPU, CUDA or cuDNN is missing, instead of silently using the CPU.
#>
param([ValidateSet('TripoSR', 'SF3D', 'SPAR3D')][string]$Engine = 'TripoSR', [switch]$Gpu)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ($PSVersionTable.PSVersion.Major -lt 7) {
    # Windows PowerShell 5.1 (the default terminal on a fresh Windows): hand over to PowerShell 7.
    if (-not (Get-Command pwsh -ErrorAction SilentlyContinue)) { throw 'PowerShell 7 is required: winget install Microsoft.PowerShell' }
    & pwsh -NoProfile -File $PSCommandPath @PSBoundParameters
    return
}

if ($Engine -in 'SF3D', 'SPAR3D') {
    if ($Gpu) { Write-Warning '-Gpu applies to TripoSR; SF3D/SPAR3D pick their device themselves (SF3D_DEVICE).' }
    $inferenceUrl = if ($Engine -eq 'SPAR3D') { 'http://127.0.0.1:5092' } else { 'http://127.0.0.1:5091' }
    Start-Process pwsh -ArgumentList '-NoExit', '-File', "$root/scripts/run-sf3d.ps1", '-Engine', $Engine
}
elseif ($Gpu) {
    . (Join-Path $PSScriptRoot 'gpu.ps1')
    $check = Test-CudaSetup
    Write-CudaReport $check
    if (-not $check.Ok) { throw 'TripoSR cannot use the GPU: install what is listed above (see README, "GPU (CUDA)"), or start without -Gpu to use the CPU.' }

    $inferenceUrl = 'http://127.0.0.1:5090'
    $pathPrefix = ($check.PathAdditions | ForEach-Object { "$_;" }) -join ''
    # Inference runs in Release with the GPU build of ONNX Runtime; the CUDA provider is required, not optional.
    $command = "`$env:PATH = '$pathPrefix' + `$env:PATH; `$env:Inference__ExecutionProvider = 'Cuda'; Set-Location '$root/src/inference/ImageTo3D.Inference'; dotnet run -c Release -p:OrtFlavor=Cuda"
    Start-Process pwsh -ArgumentList '-NoExit', '-Command', $command
}
else {
    $inferenceUrl = 'http://127.0.0.1:5090'
    # Inference runs in Release: model math is noticeably slower in Debug builds.
    Start-Process pwsh -ArgumentList '-NoExit', '-Command', "Set-Location '$root/src/inference/ImageTo3D.Inference'; dotnet run -c Release -p:OrtFlavor=Cpu"
}

Start-Process pwsh -ArgumentList '-NoExit', '-Command', "`$env:InferenceService__BaseUrl = '$inferenceUrl'; Set-Location '$root/src/backend/ImageTo3D.Api'; dotnet run"
Start-Process pwsh -ArgumentList '-NoExit', '-Command', "Set-Location '$root/src/frontend'; npm run dev"

Write-Host "Engine:    $Engine  ($inferenceUrl/health)"
Write-Host 'API:       http://localhost:5080/api/health'
Write-Host 'Web UI:    http://localhost:5173'

if ($Engine -eq 'TripoSR') {
    # Report where TripoSR actually runs once its models are loaded (first start builds and loads ~3 GB).
    Write-Host 'Waiting for TripoSR to load its models...'
    $deadline = (Get-Date).AddMinutes(10)
    while ((Get-Date) -lt $deadline) {
        try {
            $health = Invoke-RestMethod "$inferenceUrl/health" -TimeoutSec 5 -SkipHttpErrorCheck
            if ($health.status -eq 'ok') {
                $device = if ($health.executionProvider -eq 'Cuda') { 'GPU (CUDA)' } else { 'CPU' }
                Write-Host "TripoSR ready on $device. Open http://localhost:5173" -ForegroundColor Green
                return
            }
            if ($health.status -eq 'unavailable') {
                Write-Host "TripoSR failed to start: $($health.reason)" -ForegroundColor Red
                Write-Host 'See the inference window for details.'
                return
            }
        }
        catch {
            # Not listening yet (still building).
        }
        Start-Sleep -Seconds 3
    }
    Write-Warning 'TripoSR did not report ready within 10 minutes; check the inference window.'
}
