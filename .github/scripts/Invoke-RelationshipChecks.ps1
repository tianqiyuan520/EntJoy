param(
    [ValidateSet('managed', 'native')][string]$Backend = 'managed',
    [switch]$Extended,
    [string]$ReportDirectory = 'ci-reports/relationships'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-TestProcess.ps1')
. (Join-Path $PSScriptRoot 'RelationshipResults.ps1')
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Push-Location $root
try {
    $application = 'tools/RelationshipValidation/bin/Release/net8.0/RelationshipValidation.dll'
    if (-not (Test-Path -LiteralPath $application)) { throw 'Build RelationshipValidation in Release before running this gate.' }
    New-Item -ItemType Directory -Force $ReportDirectory | Out-Null
    $cases = @(
        @{ mode = 'multi-stress'; size = $(if ($Extended) { 125000 } else { 4096 }); steps = 1 },
        @{ mode = 'multi-steady'; size = $(if ($Extended) { 4096 } else { 512 }); steps = $(if ($Extended) { 1000000 } else { 20000 }) },
        @{ mode = 'bench'; size = $(if ($Extended) { 1000000 } else { 100000 }); steps = 1 }
    )
    $summary = @('| Mode | Backend | Size | Steps | Result |', '|---|---|---:|---:|---|')
    foreach ($case in $cases) {
        $resultPath = Join-Path $ReportDirectory "$($case.mode)-$Backend.jsonl"
        $logPath = Join-Path $ReportDirectory "$($case.mode)-$Backend.log"
        $arguments = @($application, '--mode', $case.mode, '--backend', $Backend, '--size', "$($case.size)", '--steps', "$($case.steps)",
            '--degree', '8', '--layout', 'both', '--out', $resultPath)
        $exitCode = Invoke-TestProcess -FilePath (Get-Command dotnet).Source -Arguments $arguments -LogPath $logPath -TimeoutSeconds 900
        if ($exitCode -ne 0) { throw "Relationship $($case.mode)/$Backend failed with exit $exitCode" }
        $records = @(Test-RelationshipResult -Path $resultPath -Mode $case.mode -Backend $Backend -Size $case.size -Steps $case.steps)
        $summary += "| $($case.mode) | $Backend | $($case.size) | $($case.steps) | PASS |"
        $records | Where-Object { $_.kind -eq 'multi-cost' -or $_.kind -eq 'cost' -or $_.kind -eq 'multi-steady-complete' } |
            Export-Csv -LiteralPath (Join-Path $ReportDirectory "$($case.mode)-$Backend.csv") -NoTypeInformation -Encoding utf8
    }
    $summary | Set-Content -LiteralPath (Join-Path $ReportDirectory 'summary.md') -Encoding utf8
    if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
    Write-Host 'PASS: relationship scale, steady and batch-allocation gates.'
}
finally { Pop-Location }
