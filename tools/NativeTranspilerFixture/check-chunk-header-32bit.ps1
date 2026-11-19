# Negative contract: on 32-bit targets the chunk context header must fail to compile.
# Native side pins the x64 layout with static_assert (88 bytes); the managed side checks the
# same numbers in ChunkContextHeaderContractTests.
# Usage: powershell -File tools/NativeTranspilerFixture/check-chunk-header-32bit.ps1 [-Compiler <path>]
param(
    [string]$Compiler = 'C:\msys64\ucrt64\bin\g++.exe'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$include = Join-Path $root 'src\NativeDll'
if (-not (Test-Path (Join-Path $include 'NativeGeneratedSupport.h'))) {
    throw "NativeGeneratedSupport.h not found: $include"
}
if (-not (Test-Path $Compiler)) {
    throw "Compiler not found: $Compiler (pass -Compiler with a g++ that supports -m32)"
}

$stamp = [guid]::NewGuid().ToString('N')
$probe = Join-Path ([System.IO.Path]::GetTempPath()) ('chunk-header-32bit-{0}.cpp' -f $stamp)
try {
    $source = "#include ""NativeGeneratedSupport.h""`nint main() { return (int)(__EntJoyChunkContextHeaderFingerprint() & 1); }`n"
    Set-Content -Path $probe -Value $source -Encoding ascii

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $text = (& $Compiler -m32 -std=c++20 -I $include -c $probe -o ($probe + '.o') 2>&1 | Out-String)
        $exit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($exit -eq 0) {
        throw "32-bit build unexpectedly succeeded: static_assert did not fire ($Compiler)"
    }
    if ($text -notmatch 'ChunkContextHeader|sizeof') {
        throw "32-bit build failed for another reason:`n$text"
    }
    Write-Host "  [ok] 32-bit target rejected by static_assert ($Compiler)"
    Write-Host ($text.Trim().Split("`n")[0])
}
finally {
    Remove-Item $probe, ($probe + '.o') -Force -ErrorAction SilentlyContinue
}
