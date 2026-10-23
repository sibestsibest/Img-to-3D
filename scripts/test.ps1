<#
.SYNOPSIS
  Runs all automated tests (.NET, frontend, and the parametric worker's pytest suite when its
  environment exists). Real-model smoke tests run automatically when ./models is populated and
  are skipped otherwise; use -SkipRealModel to exclude them explicitly.
#>
[CmdletBinding()]
param([switch]$SkipRealModel)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Push-Location $root
try {
    $filter = if ($SkipRealModel) { @('--filter', 'Category!=RealModel') } else { @() }
    dotnet test ImageTo3D.slnx @filter
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tests failed' }

    Push-Location src/frontend
    npm test
    if ($LASTEXITCODE -ne 0) { throw 'frontend tests failed' }
    Pop-Location

    # Parametric geometry worker: only when its environment exists (scripts/setup.ps1 with uv).
    $python = Join-Path $root 'src/parametric-geometry/.venv/Scripts/python.exe'
    if (Test-Path $python) {
        Push-Location src/parametric-geometry
        & $python -m pytest -q
        if ($LASTEXITCODE -ne 0) { throw 'parametric worker tests failed' }
        Pop-Location
    }
    else {
        Write-Warning 'Parametric worker tests skipped: src/parametric-geometry/.venv not found (run scripts/setup.ps1 with uv).'
    }
}
finally {
    Pop-Location
}
