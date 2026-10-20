<#
.SYNOPSIS
  Starts the Stability AI inference service: SF3D on http://127.0.0.1:5091 (default) or
  SPAR3D on http://127.0.0.1:5092 (-Engine SPAR3D; set up with setup-sf3d.ps1 -Engine SPAR3D).
  Point the API at it with:  $env:InferenceService__BaseUrl = 'http://127.0.0.1:5091'
#>
param(
    [ValidateSet('SF3D', 'SPAR3D')][string]$Engine = 'SF3D',
    [int]$Port = 0
)

$ErrorActionPreference = 'Stop'
$service = Resolve-Path (Join-Path $PSScriptRoot '..\src\inference-sf3d')
Set-Location $service
if ($Engine -eq 'SPAR3D') {
    $env:SF3D_ENGINE = 'spar3d'
    $python = '.venv-spar3d\Scripts\python.exe'
    if ($Port -eq 0) { $Port = 5092 }
}
else {
    $env:SF3D_ENGINE = 'sf3d'
    $python = '.venv\Scripts\python.exe'
    if ($Port -eq 0) { $Port = 5091 }
}
& $python -m app --host 127.0.0.1 --port $Port
