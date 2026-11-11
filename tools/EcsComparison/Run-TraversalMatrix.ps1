param(
    [string]$UnityEditor = 'C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Unity.exe',
    [switch]$QueryFocus
)

# 原生进程（dotnet / Unity）写 stderr 会被提升成终止错误，与 $ErrorActionPreference='Stop' 冲突；
# 本脚本对每次原生调用都显式检查 $LASTEXITCODE，因此按 Continue 处理，失败由退出码判定。
$ErrorActionPreference = 'Continue'
if (-not (Test-Path -LiteralPath $UnityEditor)) { throw "Unity Editor missing: $UnityEditor" }
$matrixPreviousTiering = $env:DOTNET_TieredCompilation
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Path 'obj' -Force | Out-Null
    dotnet build EcsComparison.csproj -c Release *> obj/matrix-build.txt
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; see obj/matrix-build.txt' }
    $env:DOTNET_TieredCompilation = '0'
    $matrixDll = 'bin/Release/net8.0/EcsComparison.dll'
    dotnet $matrixDll --verify-traversal-matrix *> obj/matrix-dotnet-verify.txt
    if ($LASTEXITCODE -ne 0) { throw 'Managed/native verification failed; see obj/matrix-dotnet-verify.txt' }
    foreach ($matrixPhase in @('Verify', 'Run')) {
        if ($matrixPhase -eq 'Run') {
            Start-Sleep -Seconds 5
            dotnet $matrixDll --traversal-matrix *> obj/matrix-dotnet-results.txt
            if ($LASTEXITCODE -ne 0) { throw 'EntJoy/Arch measurement failed; see obj/matrix-dotnet-results.txt' }
        }
        $matrixMethod = if ($QueryFocus -and $matrixPhase -eq 'Run') { 'RunBurstQueries' } else { $matrixPhase }
        $matrixLog = if ($matrixPhase -eq 'Verify') { 'matrix-unity-verify.log' }
            elseif ($QueryFocus) { 'matrix-unity-query-results.log' } else { 'matrix-unity-results.log' }
        Remove-Item -LiteralPath (Join-Path $PSScriptRoot "obj/$matrixLog") -ErrorAction SilentlyContinue
        $matrixArguments = @(
            '-batchmode', '-nographics',
            '-projectPath', ('"' + (Join-Path $PSScriptRoot 'UnityComparison') + '"'),
            '-executeMethod', "TraversalMatrix.$matrixMethod",
            '-logFile', ('"' + (Join-Path $PSScriptRoot "obj/$matrixLog") + '"')
        )
        $matrixUnity = Start-Process -FilePath $UnityEditor -ArgumentList $matrixArguments -WindowStyle Hidden -Wait -PassThru
        if ($matrixUnity.ExitCode -ne 0) { throw "Unity $matrixPhase failed; see obj/$matrixLog" }
    }
    & (Join-Path $PSScriptRoot 'Export-TraversalMatrix.ps1') -QueryFocus:$QueryFocus
}
finally {
    $env:DOTNET_TieredCompilation = $matrixPreviousTiering
    Pop-Location
}
