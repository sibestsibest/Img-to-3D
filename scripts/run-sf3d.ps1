<#
.SYNOPSIS
  Starts the Stable Fast 3D inference service on http://127.0.0.1:5091.
  Point the API at it with:  $env:InferenceService__BaseUrl = 'http://127.0.0.1:5091'
#>
param([int]$Port = 5091)

$ErrorActionPreference = 'Stop'
$service = Resolve-Path (Join-Path $PSScriptRoot '..\src\inference-sf3d')
Set-Location $service
& .venv\Scripts\python.exe -m app --host 127.0.0.1 --port $Port
