$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-TestProcess.ps1')
$probeDirectory = Join-Path ([IO.Path]::GetTempPath()) ('EntJoyProcessProbe_' + [Guid]::NewGuid().ToString('N'))
try {
    foreach ($expected in 0, 7) {
        $actual = Invoke-TestProcess -FilePath $env:ComSpec -Arguments @('/c', 'exit', "$expected") -LogPath (Join-Path $probeDirectory "exit-$expected.log")
        if ($actual -ne $expected) { throw "Exit code changed: expected=$expected actual=$actual" }
    }
    $timeoutLog = Join-Path $probeDirectory 'timeout.log'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $shellExecutable = (Get-Process -Id $PID).Path
    $actual = Invoke-TestProcess -FilePath $shellExecutable -Arguments @('-NoProfile', '-Command', 'Start-Sleep -Seconds 30') -LogPath $timeoutLog -TimeoutSeconds 1
    if ($actual -ne 124 -or $timer.Elapsed.TotalSeconds -ge 15) { throw 'Hung process was not terminated within the deadline.' }
    if (-not (Select-String -LiteralPath $timeoutLog -Pattern '^TIMEOUT:' -Quiet)) { throw 'Timeout diagnostic was not saved.' }
    Write-Host 'PASS: native exit codes and bounded deadlock detection.'
}
finally {
    Get-ChildItem -LiteralPath $probeDirectory -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Remove-Item -LiteralPath $probeDirectory -ErrorAction SilentlyContinue
}
