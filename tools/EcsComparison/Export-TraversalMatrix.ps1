param([switch]$QueryFocus)

$ErrorActionPreference = 'Stop'
$matrixLogs = @('matrix-dotnet-results.txt', $(if ($QueryFocus) { 'matrix-unity-query-results.log' } else { 'matrix-unity-results.log' }))
$matrixExpectedRows = if ($QueryFocus) { 88 } else { 144 }
$matrixPrefix = if ($QueryFocus) { 'traversal-focused' } else { 'traversal' }
$matrixRows = @(
    foreach ($matrixLog in $matrixLogs) {
        foreach ($matrixLine in Get-Content (Join-Path $PSScriptRoot "obj/$matrixLog")) {
            if ($matrixLine -notmatch '^MATRIX_RESULT ') { continue }
            $matrixValues = [ordered]@{}
            foreach ($matrixPair in [regex]::Matches($matrixLine, '(\w+)=(\S+)')) {
                $matrixValues[$matrixPair.Groups[1].Value] = $matrixPair.Groups[2].Value
            }
            if ($matrixValues['verified'] -ne 'true') { throw 'Unverified result' }
            if (-not $matrixValues['kernel']) { throw 'Missing kernel classification' }
            if ($matrixValues['warmup'] -ne '512' -or $matrixValues['samples'] -ne '12' -or $matrixValues['repetitions'] -ne '1024') {
                throw 'Traversal timing protocol mismatch'
            }
            if ($matrixValues['values_us'].Split(',').Count -ne 12) { throw 'Missing timing samples' }
            [pscustomobject]$matrixValues
        }
    }
)
if ($matrixRows.Count -ne $matrixExpectedRows) { throw "Expected $matrixExpectedRows results, found $($matrixRows.Count)" }
$matrixSummary = @(
    foreach ($matrixGroup in $matrixRows | Group-Object framework, layer, api, backend, kernel, entities, components) {
        $matrixRounds = @($matrixGroup.Group | Sort-Object { [int]$_.round })
        if ($matrixRounds.Count -ne 2 -or $matrixRounds[0].round -ne '1' -or $matrixRounds[1].round -ne '2') {
            throw "Missing or duplicate round: $($matrixGroup.Name)"
        }
        $matrixFirst = $matrixRounds[0]
        if ($matrixFirst.chunks -ne $matrixRounds[1].chunks) { throw "Chunk count changed: $($matrixGroup.Name)" }
        $matrixMedians = @($matrixRounds | ForEach-Object {
            [double]::Parse($_.median_us, [cultureinfo]::InvariantCulture)
        })
        $matrixAllocations = @($matrixRounds | ForEach-Object {
            [double]::Parse($_.bytes_per_call, [cultureinfo]::InvariantCulture)
        })
        [pscustomobject][ordered]@{
            framework = $matrixFirst.framework
            layer = $matrixFirst.layer
            api = $matrixFirst.api
            backend = $matrixFirst.backend
            kernel = $matrixFirst.kernel
            entities = $matrixFirst.entities
            components = $matrixFirst.components
            chunks = $matrixFirst.chunks
            round1_us = $matrixFirst.median_us
            round2_us = $matrixRounds[1].median_us
            mean_median_us = (($matrixMedians[0] + $matrixMedians[1]) / 2).ToString('F3', [cultureinfo]::InvariantCulture)
            round_ratio = (($matrixMedians | Measure-Object -Maximum).Maximum / ($matrixMedians | Measure-Object -Minimum).Minimum).ToString('F3', [cultureinfo]::InvariantCulture)
            bytes_per_call = ($matrixAllocations | Measure-Object -Maximum).Maximum
        }
    }
)
$matrixOutput = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Path $matrixOutput -Force | Out-Null
$matrixRows | Export-Csv (Join-Path $matrixOutput "$matrixPrefix-matrix.csv") -NoTypeInformation -Encoding UTF8
$matrixSummary | Export-Csv (Join-Path $matrixOutput "$matrixPrefix-summary.csv") -NoTypeInformation -Encoding UTF8
$matrixEnvironment = foreach ($matrixLog in $matrixLogs) {
    Get-Content (Join-Path $PSScriptRoot "obj/$matrixLog") | Where-Object { $_ -match '^MATRIX_ENV |self-proof:' }
}
$matrixEnvironment | Select-Object -Unique | Set-Content (Join-Path $matrixOutput "$matrixPrefix-environment.txt") -Encoding UTF8
$matrixSummary | Format-Table -AutoSize
