# Build, run and merge the Flecs/EnTT arms against the .NET side.
# ASCII only: Windows PowerShell reads script files as ANSI.
#
# Policy for the C++ arms: clang, -O2 (or -O3 via -OptNumber), identical flags for
# Flecs and EnTT, and the compiler/opt tag is part of every result and grouping key.
# Repeated runs are aggregated by median: single runs drift up to 50% on this box.
param(
    [ValidateSet('clang', 'gcc')][string]$Compiler = 'clang',
    [ValidateSet(2, 3)][int]$OptNumber = 2,
    [ValidateRange(1, 10)][int]$Repeats = 3,
    [string]$ClangRoot = 'D:\llvm23\clang+llvm-23.1.1-x86_64-pc-windows-msvc',
    [string]$Sysroot = 'C:/msys64/ucrt64',
    [string]$MingwBin = 'C:\msys64\ucrt64\bin',
    [string[]]$UnityTraverseLogs = @(),
    [string]$UnityCompiler = 'il2cpp',
    [switch]$SkipBuild,
    [switch]$SkipRun,
    [switch]$SkipDotnet
)

# Continue, not Stop: the native runs write their loader proof to stderr and
# Windows PowerShell promotes that to a terminating error. Every native call is
# checked through $LASTEXITCODE below, same as Run-TraversalMatrix.ps1.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
New-Item -ItemType Directory -Force -Path results | Out-Null

# Managed arms run with tiering off, same as Run-TraversalMatrix.ps1. Left at the
# default, the same CSV mixes two JIT configurations and the 100k x2 foreach arm
# moves by 1.9x between them.
$env:DOTNET_TieredCompilation = '0'

$tag = "$Compiler-o$OptNumber"
$nativeExe = Join-Path $root "native-comparison-$tag.exe"
$outCsv = "results\native-vs-dotnet-$tag-summary.csv"
$dotnetExe = Join-Path $root '..\bin\Release\net8.0\EcsComparison.exe'
$dotnetOutDir = Join-Path $root '..\results'

function Get-ToolPaths {
    if ($Compiler -eq 'clang') {
        $cc = Join-Path $ClangRoot 'bin\clang.exe'
        $cxx = Join-Path $ClangRoot 'bin\clang++.exe'
        if (-not (Test-Path $cc)) { throw "missing clang at $cc" }
        return @{ Cc = $cc; Cxx = $cxx; Target = @('--target=x86_64-w64-windows-gnu', "--sysroot=$Sysroot") }
    }
    $cc = Join-Path $MingwBin 'gcc.exe'
    $cxx = Join-Path $MingwBin 'g++.exe'
    if (-not (Test-Path $cc)) { throw "missing gcc at $cc" }
    return @{ Cc = $cc; Cxx = $cxx; Target = @() }
}

function Invoke-Build {
    $tool = Get-ToolPaths
    $flecsObj = "flecs_$tag.o"
    & $tool.Cc @($tool.Target) "-O$OptNumber" -c third_party\flecs\distr\flecs_no_addons.c `
        -I third_party\flecs\distr -o $flecsObj
    if ($LASTEXITCODE -ne 0) { throw 'flecs object build failed' }
    & $tool.Cxx @($tool.Target) "-O$OptNumber" -std=c++20 "-DENTJOY_OPT_NUMBER=$OptNumber" `
        -I third_party\flecs\distr -I third_party\entt\src main.cpp $flecsObj -ldbghelp -o $nativeExe
    if ($LASTEXITCODE -ne 0) { throw 'native arm build failed' }
    Write-Host "built $nativeExe"
}

function Invoke-RepeatedRuns {
    if (-not (Test-Path $nativeExe)) { throw "missing $nativeExe; run without -SkipBuild" }
    if (-not $SkipDotnet -and -not (Test-Path $dotnetExe)) {
        throw "missing $dotnetExe; build tools\EcsComparison first"
    }
    for ($i = 1; $i -le $Repeats; $i++) {
        Write-Host "repeat $i/$Repeats native"
        & $nativeExe --rounds=2 2>&1 | Out-File -Encoding utf8 "results\run$i-native-$tag.log"
        if ($LASTEXITCODE -ne 0) { throw "native run $i failed" }
        if (-not $SkipDotnet) {
            Write-Host "repeat $i/$Repeats dotnet"
            & $dotnetExe --traversal-matrix 2>&1 | Out-File -Encoding utf8 (Join-Path $dotnetOutDir "run$i-dotnet-matrix.log")
            if ($LASTEXITCODE -ne 0) { throw "dotnet run $i failed" }
        }
    }
}

function Read-MatrixRows {
    param([string[]]$Paths)
    $rows = @()
    foreach ($path in $Paths) {
        if (-not (Test-Path $path)) { continue }
        foreach ($line in Get-Content $path) {
            if ($line -notmatch '^MATRIX_RESULT') { continue }
            $kv = @{}
            foreach ($m in [regex]::Matches($line, '(\w+)=([^\s]+)')) { $kv[$m.Groups[1].Value] = $m.Groups[2].Value }
            $rows += [pscustomobject]@{
                Framework  = $kv['framework']
                Layer      = $kv['layer']
                Api        = $kv['api']
                Backend    = $kv['backend']
                Kernel     = $kv['kernel']
                Compiler   = if ($kv['compiler']) { $kv['compiler'] } elseif ($kv['framework'] -eq 'Unity') { $UnityCompiler } else { 'dotnet' }
                Opt        = if ($kv['opt']) { $kv['opt'] } else { 'jit' }
                Entities   = [int]$kv['entities']
                Components = [int]$kv['components']
                Chunks     = [int]$kv['chunks']
                MedianUs   = [double]$kv['median_us']
            }
        }
    }
    return $rows
}

function Get-Median {
    param([double[]]$Values)
    $sorted = $Values | Sort-Object
    $n = $sorted.Count
    if ($n -eq 0) { return 0 }
    if ($n % 2 -eq 1) { return $sorted[[int](($n - 1) / 2)] }
    return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2
}

if (-not $SkipBuild) { Invoke-Build }
if (-not $SkipRun) { Invoke-RepeatedRuns }

$nativeLogs = 1..$Repeats | ForEach-Object { "results\run$_-native-$tag.log" }
$dotnetLogs = 1..$Repeats | ForEach-Object { Join-Path $dotnetOutDir "run$_-dotnet-matrix.log" }
$all = @()
$all += Read-MatrixRows $nativeLogs
$all += Read-MatrixRows $dotnetLogs
if ($UnityTraverseLogs.Count -gt 0) { $all += Read-MatrixRows $UnityTraverseLogs }
if ($all.Count -eq 0) { throw 'no MATRIX_RESULT rows parsed' }

# compiler/opt join the grouping key: clang -O2 and clang -O3 are separate arms.
$summary = foreach ($group in ($all | Group-Object Framework, Layer, Api, Backend, Kernel, Compiler, Opt, Entities, Components)) {
    $values = [double[]]($group.Group | ForEach-Object { $_.MedianUs })
    $median = Get-Median $values
    $min = ($values | Measure-Object -Minimum).Minimum
    $max = ($values | Measure-Object -Maximum).Maximum
    [pscustomobject]@{
        framework  = $group.Group[0].Framework
        layer      = $group.Group[0].Layer
        api        = $group.Group[0].Api
        backend    = $group.Group[0].Backend
        kernel     = $group.Group[0].Kernel
        compiler   = $group.Group[0].Compiler
        opt        = $group.Group[0].Opt
        entities   = $group.Group[0].Entities
        components = $group.Group[0].Components
        chunks     = $group.Group[0].Chunks
        samples    = $values.Count
        median_us  = [Math]::Round($median, 3)
        min_us     = [Math]::Round($min, 3)
        max_us     = [Math]::Round($max, 3)
        spread     = [Math]::Round(($max / $min), 3)
    }
}

$summary | Sort-Object entities, components, median_us |
    Export-Csv -Path $outCsv -NoTypeInformation -Encoding UTF8

Write-Host "rows=$($summary.Count) samples_per_arm=$($Repeats * 2) out=$outCsv"
$summary | Where-Object { $_.entities -eq 100000 } | Sort-Object components, median_us |
    Format-Table framework, layer, api, backend, compiler, opt, median_us, min_us, max_us, spread -AutoSize
