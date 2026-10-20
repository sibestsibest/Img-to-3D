<#
.SYNOPSIS
  Runs all automated tests. Real-model smoke tests run automatically when ./models is
  populated and are skipped otherwise; use -SkipRealModel to exclude them explicitly.
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
}
finally {
    Pop-Location
}
