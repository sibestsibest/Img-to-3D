<#
.SYNOPSIS
  Helpers for running TripoSR on an NVIDIA GPU (CUDA). Dot-sourced by setup.ps1 and dev.ps1.

.DESCRIPTION
  ONNX Runtime 1.31 (Microsoft.ML.OnnxRuntime.Gpu) is built with CUDA 13 and needs cuDNN 9; its
  NuGet package does not include those libraries. They are found on PATH, under CUDA_PATH and in
  the default cuDNN install folder; directories that hold them but are not on PATH are returned so
  the inference process can be started with them added.
#>

$script:CudaRuntimeDll = 'cudart64_13*.dll'
$script:CudnnDll = 'cudnn64_9.dll'
$script:MinDriverMajor = 580 # first driver branch that supports CUDA 13

function Get-NvidiaGpu {
    <# Name, VRAM (MB) and driver of the first NVIDIA GPU, or $null when nvidia-smi is missing or fails. #>
    $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if (-not $smi) {
        $default = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
        if (Test-Path $default) { $smi = Get-Item $default } else { return $null }
    }
    # Read all lines first: stopping the pipeline early (Select-Object -First) would kill the process and fail the exit code.
    $lines = @(& $smi.Source --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits 2>$null)
    if ($LASTEXITCODE -ne 0 -or $lines.Count -eq 0 -or -not $lines[0]) { return $null }
    $line = $lines[0]
    $parts = $line -split ',\s*'
    [pscustomobject]@{ Name = $parts[0]; MemoryMb = [int]$parts[1]; Driver = $parts[2] }
}

function Find-DllDirectory([string]$pattern, [string[]]$extraRoots) {
    <# First directory holding a file matching $pattern: PATH first, then the extra roots (searched recursively). #>
    foreach ($dir in ($env:PATH -split ';' | Where-Object { $_ -and (Test-Path $_) })) {
        if (Get-ChildItem -Path $dir -Filter $pattern -File -ErrorAction SilentlyContinue | Select-Object -First 1) { return $dir }
    }
    foreach ($root in $extraRoots | Where-Object { $_ -and (Test-Path $_) }) {
        $hit = Get-ChildItem -Path $root -Filter $pattern -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.DirectoryName }
    }
    return $null
}

function Test-CudaSetup {
    <#
      Checks driver, GPU memory, CUDA 13 runtime and cuDNN 9. Returns
      { Ok, Gpu, Problems[], Warnings[], PathAdditions[] }; PathAdditions are the library folders
      found outside PATH.
    #>
    $problems = @()
    $warnings = @()
    $gpu = Get-NvidiaGpu
    if (-not $gpu) {
        $problems += 'No NVIDIA GPU found (nvidia-smi is missing or failed). Install the latest NVIDIA driver: https://www.nvidia.com/Download/index.aspx'
    }
    else {
        $major = [int](($gpu.Driver -split '\.')[0])
        if ($major -lt $script:MinDriverMajor) {
            $problems += "NVIDIA driver $($gpu.Driver) is too old for CUDA 13 (needs $($script:MinDriverMajor) or newer). Update it: https://www.nvidia.com/Download/index.aspx"
        }
        if ($gpu.MemoryMb -lt 6000) {
            $warnings += "$($gpu.Name) has $([math]::Round($gpu.MemoryMb / 1024, 1)) GB of video memory; TripoSR needs about 6 GB. If generation fails with out_of_memory, lower Inference:ChunkSize (e.g. 65536)."
        }
    }

    $cudaRoots = @($env:CUDA_PATH) + @(Get-ChildItem 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA' -Directory -Filter 'v13*' -ErrorAction SilentlyContinue | ForEach-Object FullName)
    $cudart = Find-DllDirectory $script:CudaRuntimeDll $cudaRoots
    if (-not $cudart) {
        $problems += 'CUDA 13 runtime (cudart64_13.dll) not found. Install CUDA Toolkit 13: https://developer.nvidia.com/cuda-downloads (Windows, x86_64).'
    }

    $cudnn = Find-DllDirectory $script:CudnnDll (@('C:\Program Files\NVIDIA\CUDNN') + $cudaRoots)
    if (-not $cudnn) {
        $problems += 'cuDNN 9 (cudnn64_9.dll) not found. Install cuDNN 9 for CUDA 13: https://developer.nvidia.com/cudnn-downloads (Windows, x86_64, CUDA 13).'
    }

    $onPath = $env:PATH -split ';'
    $additions = @($cudart, $cudnn) | Where-Object { $_ -and ($onPath -notcontains $_) } | Select-Object -Unique
    [pscustomobject]@{ Ok = $problems.Count -eq 0; Gpu = $gpu; Problems = $problems; Warnings = $warnings; PathAdditions = @($additions) }
}

function Write-CudaReport($check) {
    if ($check.Gpu) { Write-Host "GPU:    $($check.Gpu.Name), $([math]::Round($check.Gpu.MemoryMb / 1024, 1)) GB, driver $($check.Gpu.Driver)" }
    foreach ($w in $check.Warnings) { Write-Warning $w }
    foreach ($p in $check.Problems) { Write-Host "  missing: $p" -ForegroundColor Red }
    if ($check.PathAdditions.Count -gt 0) { Write-Host "CUDA libraries found outside PATH (added for the inference service): $($check.PathAdditions -join '; ')" }
}
