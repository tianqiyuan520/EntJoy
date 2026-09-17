# Jobs-only regression guard (CI, invoked by .github/workflows/jobsystem-ci.yml framework-test).
#
# What it protects: a consumer referencing EntJoy.Collections / EntJoy.Jobs / EntJoy.Mathematics
# plus the NativeTranspiler analyzer -- but NOT EntJoy.ECS -- must be able to use [NativeTranspile]
# array jobs. Backed by the conditional emission in
# src/NativeTranspiler/Analyzer/Common/BindingsGenerator.cs (ECS usings, ChunkJobFuncDelegate and the
# World parameter are emitted only when a chunk-scheduled job exists).
#
# Assertions:
#   [1] the project builds with 0 errors
#   [2] the generated bindings contain no ECS coupling
#   [3] the project itself declares no EntJoy.ECS reference (the guard cannot be bypassed)
#   [4] array-job Schedule_* signatures carry no "World" parameter
#
# Exit code: 0 = guard holds; 1 = coupling regression or a bypassed guard.
# ASCII only on purpose: Windows PowerShell 5.1 reads non-BOM UTF-8 as ANSI and mangles it.

$ErrorActionPreference = 'Continue'

$root = $PSScriptRoot
$proj = Join-Path $root 'JobsOnlyTranspilerCheck.csproj'

# ---- [3] a guard that can be silenced by adding a reference proves nothing ----
$refLines = Select-String -Path $proj -Pattern 'ProjectReference\s+Include' | ForEach-Object { $_.Line }
$ecsRefs = $refLines | Where-Object { $_ -match 'EntJoy\.ECS' }
if ($ecsRefs) {
    Write-Host 'FAIL[3]: the check project references EntJoy.ECS -- guard bypassed, not satisfied:'
    $ecsRefs | ForEach-Object { Write-Host "  $_" }
    exit 1
}

# ---- [1] must compile without ECS ----
$output = & dotnet build $proj -c Release --nologo 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host 'FAIL[1]: jobs-only build failed -- generated bindings likely need an ECS reference again:'
    Write-Host $output
    exit 1
}

# ---- [5] the generator's own decoupling invariant must not fire on an ECS-free project ----
# NT029 = chunk/entity jobs seen without EntJoy.ECS; NT030 = generated bindings leak ECS symbols.
if ($output -match 'NT0(29|30)') {
    Write-Host 'FAIL[5]: the generator decoupling invariant fired on an ECS-free project:'
    ($output -split "`n") | Where-Object { $_ -match 'NT0(29|30)' } | Select-Object -First 3 |
        ForEach-Object { Write-Host ("  " + $_.Trim()) }
    exit 1
}

# ---- locate the generated bindings ----
$gen = Get-ChildItem -Path (Join-Path $root 'obj') -Recurse -Filter 'NativeTranspiler.Bindings.g.cs' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $gen) {
    Write-Host 'FAIL: NativeTranspiler.Bindings.g.cs was not emitted -- nothing was verified.'
    exit 1
}
$content = Get-Content $gen.FullName -Raw

# ---- [2] generated output must not re-introduce ECS coupling ----
$hits = Select-String -Path $gen.FullName -Pattern 'EntJoy\.ECS|ChunkJobData|ChunkEnabledMask|ArchetypeChunk|QueryBuilder|EntityManager|World'
if ($hits) {
    Write-Host 'FAIL[2]: generated bindings reference ECS again:'
    $hits | Select-Object -First 10 | ForEach-Object { Write-Host ("  line {0}: {1}" -f $_.LineNumber, $_.Line.Trim()) }
    exit 1
}

# ---- [4] array-job Schedule_* signatures must not carry a World parameter ----
if ($content -match 'Schedule_\w+\([^)]*\bWorld\b') {
    Write-Host 'FAIL[4]: an array-job Schedule_* signature carries a World parameter:'
    [regex]::Matches($content, 'public static JobHandle Schedule_\w+\([^)]*\)') |
        ForEach-Object { Write-Host "  $($_.Value)" }
    exit 1
}

Write-Host ('PASS: jobs-only usage holds -- generated bindings {0} bytes, zero ECS coupling.' -f $content.Length)
exit 0
