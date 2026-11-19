# Merge the .NET structural-change logs (EntJoy, Arch), the native one (Flecs, EnTT)
# and the Unity player one into a single tier x framework summary.
# ASCII only: Windows PowerShell reads script files as ANSI.
#
# All sides use the same protocol: fresh world per operation, resets outside the timed
# region, minimum over rounds, 25 rounds. Logs may contain several STRUCT_HEAD blocks
# (one per entity tier).
param(
    [string[]]$DotnetStructLogs = @(
        '..\results\struct-1024.log',
        '..\results\struct-10000.log',
        '..\results\struct-20261115.log'
    ),
    [string[]]$NativeStructLogs = @('results\struct-clang-o2-tiers.log'),
    [string[]]$UnityStructLogs = @('..\results\struct-unity-il2cpp.log'),
    [string]$UnityCompiler = 'il2cpp',
    [string]$OutCsv = 'results\struct-vs-native-summary.csv'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Read-StructRows {
    param([string]$Path, [string]$UnityTag = 'il2cpp')
    if (-not (Test-Path $Path)) { Write-Warning "missing log $Path"; return @() }
    $rows = @()
    $entities = 0
    $compiler = 'dotnet'
    foreach ($line in Get-Content $Path) {
        if ($line -match '^STRUCT_HEAD') {
            $m = [regex]::Match($line, 'entities=(\d+)')
            if ($m.Success) { $entities = [int]$m.Groups[1].Value }
            $c = [regex]::Match($line, 'compiler=([^\s]+)')
            if ($c.Success) { $compiler = $c.Groups[1].Value }
            continue
        }
        if ($line -notmatch '^STRUCT_ROW') { continue }
        $kv = @{}
        foreach ($m in [regex]::Matches($line, '(\w+)=([^\s]+)')) { $kv[$m.Groups[1].Value] = $m.Groups[2].Value }
        # Unity 的 STRUCT_HEAD 可能与实际后端不符（同一份 Player 源码跑两种后端），
        # 以调用方的 -UnityCompiler 为准。
        $rowCompiler = $compiler
        if ($kv['framework'] -eq 'Unity') { $rowCompiler = $UnityTag }
        elseif ($kv['compiler']) { $rowCompiler = $kv['compiler'] }
        $rows += [pscustomobject]@{
            Op        = $kv['op']
            Framework = $kv['framework']
            Compiler  = $rowCompiler
            Opt       = if ($kv['opt']) { $kv['opt'] } else { 'jit' }
            Entities  = $entities
            Us        = [double]$kv['us']
        }
    }
    return $rows
}

$all = @()
foreach ($path in $DotnetStructLogs) { $all += Read-StructRows $path }
foreach ($path in $NativeStructLogs) { $all += Read-StructRows $path }
foreach ($path in $UnityStructLogs) { $all += Read-StructRows -Path $path -UnityTag $UnityCompiler }
if ($all.Count -eq 0) { throw 'no STRUCT_ROW rows parsed' }

$best = $all | Group-Object Op, Framework, Compiler, Opt, Entities | ForEach-Object {
    $rows = $_.Group
    [pscustomobject]@{
        op        = $rows[0].Op
        framework = $rows[0].Framework
        compiler  = $rows[0].Compiler
        opt       = $rows[0].Opt
        entities  = $rows[0].Entities
        min_us    = ($rows | Measure-Object -Property Us -Minimum).Minimum
    }
}

$summary = foreach ($row in $best) {
    $base = ($best | Where-Object {
        $_.entities -eq $row.entities -and $_.framework -eq 'Arch' -and $_.op -eq $row.op
    } | Select-Object -First 1).min_us
    [pscustomobject]@{
        entities      = $row.entities
        op            = $row.op
        framework     = $row.framework
        compiler      = $row.compiler
        opt           = $row.opt
        min_us        = [Math]::Round($row.min_us, 1)
        ns_per_entity = [Math]::Round($row.min_us * 1000.0 / $row.entities, 2)
        ratio_vs_arch = if ($base -and $base -gt 0) { [Math]::Round($row.min_us / $base, 4) } else { '' }
    }
}

$order = @{ create = 0; add = 1; remove = 2; destroy = 3 }
$summary | Sort-Object entities, @{ Expression = { $order[$_.op] } }, ns_per_entity |
    Export-Csv -Path $OutCsv -NoTypeInformation -Encoding UTF8

Write-Host "rows=$($summary.Count) out=$OutCsv"
$summary | Sort-Object entities, @{ Expression = { $order[$_.op] } }, ns_per_entity |
    Format-Table entities, op, framework, compiler, ns_per_entity, ratio_vs_arch -AutoSize
