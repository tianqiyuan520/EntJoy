# NuGet consumer smoke test runner.
#
# What it does:
#   1. stage the prebuilt native artifacts (tools/NativeDllCore -> artifacts/native/win-x64)
#   2. pack the four EntJoy packages into artifacts/packages (the local feed)
#   3. evict entjoy.* from the NuGet global cache so the local feed is really used
#   4. restore + build + run tests/NuGetConsumer (PackageReference only, no repo path references)
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File tests\NuGetConsumer\run.ps1
# Exit:   0 = smoke test passed; non-zero = failed (build or assertion).
#
# Requires: .NET 8 SDK, CMake + MSVC (path 3 compiles NativeTranspiled.dll locally).

$ErrorActionPreference = 'Continue'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repoRoot

function Step($text) { Write-Host "`n=== $text ===" }

$stageDir = Join-Path $repoRoot 'artifacts\native\win-x64'
$packDir = Join-Path $repoRoot 'artifacts\packages'
$cmakeBuild = Join-Path $repoRoot 'artifacts\native\cmake-build'

# ---- 1) stage prebuilt native artifacts if missing ----
if (-not (Test-Path (Join-Path $stageDir 'NativeDll.dll')) -or -not (Test-Path (Join-Path $stageDir 'NativeDll.lib'))) {
    Step 'staging prebuilt NativeDll (tools/NativeDllCore)'
    cmake -S tools/NativeDllCore -B $cmakeBuild -A x64 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host 'FAIL: cmake configure failed'; exit 1 }
    cmake --build $cmakeBuild --config Release | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host 'FAIL: cmake build failed'; exit 1 }
    New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
    Copy-Item (Join-Path $cmakeBuild 'Release\NativeDll.dll') $stageDir -Force
    Copy-Item (Join-Path $cmakeBuild 'Release\NativeDll.lib') $stageDir -Force
}

# ---- 2) pack the four packages ----
Step 'packing EntJoy packages'
Remove-Item -Recurse -Force $packDir -ErrorAction SilentlyContinue
foreach ($proj in @(
        'src\EntJoy.Mathematics\EntJoy.Mathematics.csproj',
        'src\EntJoy.Collections\EntJoy.Collections.csproj',
        'src\EntJoy.Jobs\EntJoy.Jobs.csproj',
        'src\EntJoy.ECS\EntJoy.ECS.csproj')) {
    dotnet pack $proj -c Release --nologo 2>&1 | Select-String -Pattern '已成功创建包|Successfully created package|error' | ForEach-Object { Write-Host "  $_" }
    if ($LASTEXITCODE -ne 0) { Write-Host "FAIL: pack failed for $proj"; exit 1 }
}

# ---- 3) evict cached EntJoy packages ----
Step 'evicting entjoy.* from the NuGet global cache'
$cache = Join-Path $env:USERPROFILE '.nuget\packages'
Get-ChildItem $cache -Directory -Filter 'entjoy.*' -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item -Recurse -Force $_.FullName
    Write-Host "  removed $($_.Name)"
}

# ---- 4) restore + build + run the consumers ----
#   tests\NuGetConsumer      : ECS consumer  (managed ECS + native JobSystem + transpiled chunk/entity/array jobs)
#   tests\NuGetJobsConsumer  : Jobs-only consumer (PackageReference EntJoy.Jobs only, no EntJoy.ECS)
Step 'running NuGet consumer smoke tests'
$consumers = @('tests\NuGetConsumer', 'tests\NuGetJobsConsumer')
$failed = @()

foreach ($rel in $consumers) {
    $consumerDir = Join-Path $repoRoot $rel
    Write-Host "`n--- $rel"
    Remove-Item -Recurse -Force (Join-Path $consumerDir 'bin'), (Join-Path $consumerDir 'obj') -ErrorAction SilentlyContinue
    Push-Location $consumerDir
    try {
        dotnet build -c Release --nologo | Select-String -Pattern '个错误|error' | ForEach-Object { Write-Host "  $_" }
        if ($LASTEXITCODE -ne 0) { Write-Host "FAIL: build failed for $rel"; $failed += $rel; continue }

        $exe = Join-Path $consumerDir ("bin\Release\net8.0\" + (Split-Path $rel -Leaf) + '.exe')
        & $exe
        if ($LASTEXITCODE -ne 0) { $failed += $rel }
    } finally {
        Pop-Location
    }
}

Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host 'PASS: NuGet consumer smoke tests (ECS + Jobs-only).'
    exit 0
}
Write-Host ("FAIL: " + ($failed -join ', '))
exit 1
