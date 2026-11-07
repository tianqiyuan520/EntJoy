function Test-RelationshipResult {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Mode,
        [Parameter(Mandatory)][string]$Backend,
        [Parameter(Mandatory)][int]$Size,
        [Parameter(Mandatory)][int]$Steps
    )
    $records = @(Get-Content -LiteralPath $Path -Encoding utf8 | ForEach-Object { $_ | ConvertFrom-Json -ErrorAction Stop })
    $environment = @($records | Where-Object kind -eq 'environment')
    $complete = @($records | Where-Object kind -eq 'complete')
    if ($environment.Count -ne 1 -or $complete.Count -ne 1 -or $records[-1].kind -ne 'complete' -or
        $complete[0].success -isnot [bool] -or -not $complete[0].success -or
        $complete[0].mode -ne $Mode -or $complete[0].backend -ne $Backend -or
        $environment[0].mode -ne $Mode -or $environment[0].backend -ne $Backend -or
        $environment[0].actual_backend -ne $Backend -or $environment[0].workers -ne 2 -or
        $environment[0].size -ne $Size -or $environment[0].steps -ne $Steps -or
        [string]::IsNullOrEmpty($environment[0].ecs_sha256)) {
        throw "Invalid or incomplete relationship result: $Path"
    }
    if ($Mode -eq 'multi-stress') {
        foreach ($layout in 'MultiLinks', 'FixedLinks') {
            foreach ($name in 'multi-clone', 'multi-delete-hub') {
                $samples = @($records | Where-Object { $_.kind -eq 'multi-cost' -and $_.name -eq $name -and $_.layout -eq $layout })
                if ($samples.Count -ne 3 -or (@($samples.sample | Sort-Object -Unique) -join ',') -ne '0,1,2' -or
                    @($samples | Where-Object { $_.size -ne $Size -or $_.edges -ne ([long]$Size * 8) }).Count) {
                    throw "Missing M:N scale samples: $layout/$name"
                }
            }
        }
        if (@($records | Where-Object { $_.kind -eq 'multi-cascade' -and $_.success -eq $true }).Count -ne 1 -or
            @($records | Where-Object { $_.kind -eq 'multi-enabled' -and $_.success -eq $true }).Count -ne 2 -or
            @($records | Where-Object { $_.name -eq 'multi-delete-many-targets' -and $_.edges -eq $Size }).Count -ne 3) {
            throw 'Missing M:N high-outgoing, cascade or Enabled validation.'
        }
    } elseif ($Mode -eq 'multi-steady') {
        $finished = @($records | Where-Object kind -eq 'multi-steady-complete')
        $keys = @($finished | ForEach-Object { "$($_.layout)/$($_.shape)" } | Sort-Object -Unique)
        if ($finished.Count -ne 3 -or ($keys -join ',') -ne 'FixedLinks/many-to-many,MultiLinks/high-outgoing,MultiLinks/many-to-many' -or
            @($finished | Where-Object { $_.cycles -ne $Steps -or $_.success -ne $true -or $_.chunk_reserved_bytes -ne 0 -or
                $_.postwarm_managed_growth_bytes -gt 8MB }).Count) {
            throw 'M:N steady validation did not finish all storage/shape cases or exceeded its memory bound.'
        }
        foreach ($case in $finished) {
            $expectedSize = if ($case.shape -eq 'high-outgoing') { 1 } else { $Size }
            $expectedDegree = if ($case.shape -eq 'high-outgoing') { 64 } else { 8 }
            if ($case.size -ne $expectedSize -or $case.degree -ne $expectedDegree -or $case.success -isnot [bool] -or
                'postwarm_managed_growth_bytes' -notin $case.PSObject.Properties.Name) { throw 'M:N steady case has the wrong workload or missing measurements.' }
        }
    } elseif ($Mode -eq 'bench') {
        foreach ($name in 'clone-group', 'delete-target') {
            $samples = @($records | Where-Object { $_.kind -eq 'cost' -and $_.name -eq $name -and $_.hubs -eq 1 })
            if ($samples.Count -ne 3 -or (@($samples.sample | Sort-Object -Unique) -join ',') -ne '0,1,2') { throw "Missing batch samples: $name" }
            $limit = if ($name -eq 'clone-group') { [long]$Size * 224 + 1MB } else { [long]$Size * 110 + 512KB }
            foreach ($sample in $samples) {
                if ($sample.operations -ne $Size -or $sample.allocated_bytes -lt 0 -or $sample.allocated_bytes -gt $limit) {
                    throw "Batch allocation budget exceeded: $name $($sample.allocated_bytes) > $limit bytes"
                }
            }
        }
    } else { throw "No CI result contract for mode $Mode" }
    return $records
}
