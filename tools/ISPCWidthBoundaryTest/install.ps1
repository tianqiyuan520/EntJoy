$ErrorActionPreference = 'Stop'
$zipPath = Join-Path $env:RUNNER_TEMP 'ispc.zip'
$installPath = Join-Path $env:RUNNER_TEMP 'ispc'
Invoke-WebRequest -Uri 'https://github.com/ispc/ispc/releases/download/v1.30.0/ispc-v1.30.0-windows.zip' -OutFile $zipPath
Expand-Archive -Path $zipPath -DestinationPath $installPath -Force
$ispcExe = Get-ChildItem $installPath -Recurse -Filter 'ispc.exe' | Select-Object -First 1
if (-not $ispcExe) { throw 'ISPC executable missing from archive.' }
$ispcExe.DirectoryName | Out-File -FilePath $env:GITHUB_PATH -Append -Encoding utf8
