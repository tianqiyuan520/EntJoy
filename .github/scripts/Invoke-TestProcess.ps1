function Invoke-TestProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$LogPath,
        [ValidateRange(1, 1800)][int]$TimeoutSeconds = 120
    )
    $fullLogPath = [IO.Path]::GetFullPath($LogPath)
    New-Item -ItemType Directory -Force (Split-Path $fullLogPath -Parent) | Out-Null
    $stdout = "$fullLogPath.stdout"
    $stderr = "$fullLogPath.stderr"
    $process = $null
    try {
        $parameters = @{
            FilePath = $FilePath
            WindowStyle = 'Hidden'
            PassThru = $true
            RedirectStandardOutput = $stdout
            RedirectStandardError = $stderr
        }
        if ($Arguments.Count) { $parameters.ArgumentList = $Arguments }
        $process = Start-Process @parameters
        # Retain the handle so ExitCode remains available for short-lived children.
        $processHandle = $process.Handle
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { $process.Kill(); $process.WaitForExit() }
        $result = if ($timedOut) { 124 } else { $process.ExitCode }
        $lines = @(Get-Content -LiteralPath $stdout -Encoding UTF8 -ErrorAction SilentlyContinue)
        $lines += @(Get-Content -LiteralPath $stderr -Encoding UTF8 -ErrorAction SilentlyContinue)
        if ($timedOut) { $lines += "TIMEOUT: $FilePath $($Arguments -join ' ') after ${TimeoutSeconds}s" }
        $lines | Set-Content -LiteralPath $fullLogPath -Encoding UTF8
        foreach ($line in $lines) { Write-Host $line }
        return [int]$result
    }
    finally {
        if ($null -ne $process) {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
            $process.Dispose()
        }
        Remove-Item -LiteralPath $stdout, $stderr -ErrorAction SilentlyContinue
    }
}
