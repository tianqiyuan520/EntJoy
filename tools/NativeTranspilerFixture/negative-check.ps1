# Negative gate self-check (F-3 / F-10) -- ASCII only on purpose: Windows PowerShell 5.1 reads
# non-BOM UTF-8 as ANSI, which mangles non-ASCII text and can break parsing.
#
# Adds Negative\MarkerGateJob.cs (body contains `unchecked { }`, which the generator cannot translate)
# and asserts the build FAILS:
#   generator emits `__ENTJOY_UNSUPPORTED_STMT__UncheckedStatement` -> NativeCompileTask refuses to compile.
# Historical incident: that block was silently dropped, producing a degraded C++ body while the build
# still succeeded ("compiles but computes nothing").
#
# Usage: powershell -File tools/NativeTranspilerFixture/negative-check.ps1
# Exit code: 0 = gate works (build failed as expected); 1 = gate is broken or failed for another reason.

$ErrorActionPreference = 'Continue'
$proj = Join-Path $PSScriptRoot 'NativeTranspilerFixture.csproj'

Write-Host '--- negative check: marker gate must reject the build ---'
$output = & dotnet build $proj -c Release -p:FixtureMarkerGate=true 2>&1 | Out-String
$exit = $LASTEXITCODE

if ($exit -eq 0) {
    Write-Host 'FAIL: the build succeeded -- silently degraded code was NOT blocked.'
    exit 1
}
if ($output -notmatch 'silently-degraded') {
    Write-Host 'FAIL: build failed, but not because of the marker gate (looks like a plain compile error):'
    Write-Host $output
    exit 1
}
if ($output -notmatch '__ENTJOY_UNSUPPORTED_STMT__') {
    Write-Host 'FAIL: the error has no marker name / file+line, so it cannot be located:'
    Write-Host $output
    exit 1
}

Write-Host 'PASS: marker gate fails the build and reports file+line+marker.'
exit 0
