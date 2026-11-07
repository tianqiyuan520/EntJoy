function Invoke-TestProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$LogPath,
        [ValidateRange(1, 1800)][int]$TimeoutSeconds = 120
    )
    $fullLogPath = [IO.Path]::GetFullPath($LogPath)
    New-Item -ItemType Directory -Force (Split-Path $fullLogPath -Parent) | Out-Null
    $process = [Diagnostics.Process]::new()
    $started = $false
    try {
        $process.StartInfo.WorkingDirectory = (Get-Location).ProviderPath
        $process.StartInfo.FileName = if (-not [IO.Path]::IsPathRooted($FilePath) -and ($FilePath.Contains('/') -or $FilePath.Contains('\'))) {
            [IO.Path]::GetFullPath($FilePath, $process.StartInfo.WorkingDirectory)
        } else { $FilePath }
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.CreateNoWindow = $true
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $process.StartInfo.ArgumentList.Add($argument) }
        if (-not $process.Start()) { throw "Could not start $FilePath" }
        $started = $true
        $processHandle = $process.Handle
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $result = if ($timedOut) { 124 } else { $process.ExitCode }
        $content = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($timedOut) { $content += "`nTIMEOUT: $FilePath $($Arguments -join ' ') after ${TimeoutSeconds}s`n" }
        $content | Set-Content -LiteralPath $fullLogPath -Encoding UTF8
        Write-Host $content
        return [int]$result
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
