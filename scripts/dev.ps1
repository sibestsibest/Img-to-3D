<#
.SYNOPSIS
  Starts an inference engine, the API and the Vite dev server in separate windows.
  Open http://localhost:5173 when all three are up.

.PARAMETER Engine
  TripoSR (default): .NET + ONNX Runtime service on :5090.
  SF3D: Stable Fast 3D Python service on :5091 (run scripts/setup-sf3d.ps1 first; needs HF_TOKEN).
  SPAR3D: Stable Point-Aware 3D on :5092, better hidden sides, slower (setup-sf3d.ps1 -Engine SPAR3D).
#>
param([ValidateSet('TripoSR', 'SF3D', 'SPAR3D')][string]$Engine = 'TripoSR')

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ($Engine -in 'SF3D', 'SPAR3D') {
    $inferenceUrl = if ($Engine -eq 'SPAR3D') { 'http://127.0.0.1:5092' } else { 'http://127.0.0.1:5091' }
    Start-Process pwsh -ArgumentList '-NoExit', '-File', "$root/scripts/run-sf3d.ps1", '-Engine', $Engine
}
else {
    $inferenceUrl = 'http://127.0.0.1:5090'
    # Inference runs in Release: model math is noticeably slower in Debug builds.
    Start-Process pwsh -ArgumentList '-NoExit', '-Command', "Set-Location '$root/src/inference/ImageTo3D.Inference'; dotnet run -c Release"
}

Start-Process pwsh -ArgumentList '-NoExit', '-Command', "`$env:InferenceService__BaseUrl = '$inferenceUrl'; Set-Location '$root/src/backend/ImageTo3D.Api'; dotnet run"
Start-Process pwsh -ArgumentList '-NoExit', '-Command', "Set-Location '$root/src/frontend'; npm run dev"

Write-Host "Engine:    $Engine  ($inferenceUrl/health)"
Write-Host 'API:       http://localhost:5080/api/health'
Write-Host 'Web UI:    http://localhost:5173'
