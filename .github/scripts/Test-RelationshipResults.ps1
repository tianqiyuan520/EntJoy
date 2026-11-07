$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RelationshipResults.ps1')
$probe = Join-Path ([IO.Path]::GetTempPath()) ('EntJoyRelationshipResult_' + [Guid]::NewGuid().ToString('N') + '.jsonl')
$base = @(
    @{ kind = 'environment'; mode = 'bench'; backend = 'managed'; actual_backend = 'managed'; workers = 2; size = 100000; steps = 1; ecs_sha256 = 'probe' }
)
foreach ($name in 'clone-group', 'delete-target') {
    foreach ($sample in 0..2) {
        $base += @{ kind = 'cost'; name = $name; hubs = 1; sample = $sample; operations = 100000; allocated_bytes = 1000 }
    }
}
$base += @{ kind = 'complete'; mode = 'bench'; backend = 'managed'; success = $true }
function Write-Probe($records) { $records | ForEach-Object { ConvertTo-Json -InputObject $_ -Compress } | Set-Content -LiteralPath $probe -Encoding utf8 }
function Validate-Probe { Test-RelationshipResult -Path $probe -Mode bench -Backend managed -Size 100000 -Steps 1 | Out-Null }
function Require-Rejection($name) {
    $rejected = $false
    try { Validate-Probe } catch { $rejected = $true }
    if (-not $rejected) { throw "Relationship gate accepted $name" }
}
try {
    Write-Probe $base; Validate-Probe
    Write-Probe @($base[0], $base[-1]); Require-Rejection 'zero measured operations'
    Write-Probe $base[0..($base.Count - 2)]; Require-Rejection 'missing completion'
    Write-Probe $base
    Add-Content -LiteralPath $probe -Value 'not-json'; Require-Rejection 'corrupt output'
    $base[0].actual_backend = 'native'
    Write-Probe $base; Require-Rejection 'silent backend change'
    $base[0].actual_backend = 'managed'
    $base[1].allocated_bytes = 1000000000
    Write-Probe $base; Require-Rejection 'allocation regression'
    $base[1].allocated_bytes = 1000
    $base[-1].success = 'true'
    Write-Probe $base; Require-Rejection 'string completion flag'
    Write-Host 'PASS: relationship result gate rejects empty, truncated, corrupt, fallback and over-budget runs.'
}
finally { Remove-Item -LiteralPath $probe -ErrorAction SilentlyContinue }
