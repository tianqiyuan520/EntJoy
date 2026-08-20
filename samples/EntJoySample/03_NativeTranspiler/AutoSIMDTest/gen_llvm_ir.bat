@echo off
REM 在每个 Case 目录下生成文本格式 LLVM IR
REM 用 ISPC 的 --emit-llvm-text 生成可读的 .ll 文件

set ISPC=E:/Code/ispc-v1.30.0-windows/bin/ispc.exe
set TARGET=--target=avx2-i32x8 --math-lib=fast

echo Generating textual LLVM IR files...

cd /d "%~dp0"

for %%C in (Case1_SimpleArith Case2_MathFunctions Case3_SimpleReduce Case4_ComplexControlFlow Case5_GatherReduce) do (
    for %%F in (%%C\*.ispc) do (
        set NAME=%%~nF
        echo   %%~nF...
        "%ISPC%" "%%F" --emit-llvm-text -o "%%C\%%~nF.ll" %TARGET%
    )
)

echo Done.
