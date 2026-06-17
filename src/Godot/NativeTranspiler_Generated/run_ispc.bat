@echo off
cd /d "%~dp0"
if not exist build mkdir build
setlocal enabledelayedexpansion
set ISPC=E:/Code/ispc-v1.30.0-windows/bin/ispc.exe
where ispc.exe >nul 2>nul
if not errorlevel 1 set ISPC=ispc.exe
if not exist "%ISPC%" (
    echo ISPC not found. Put ispc.exe in PATH or at E:/Code/ispc-v1.30.0-windows/bin/ispc.exe
    exit /b 1
)
set MAXCONCURRENT=%NUMBER_OF_PROCESSORS%
if "%MAXCONCURRENT%"=="" set MAXCONCURRENT=8
set FAILED=0

:wait_SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute
)

echo Compiling SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute.ispc... (fast)
start /b /min "ISPC_SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute" "%ISPC%" "SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute.ispc" -o "build\SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute.obj" -h "SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute.log" 2>&1

:skip_SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute

:wait_SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute
)

echo Compiling SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute.ispc... (fast)
start /b /min "ISPC_SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute" "%ISPC%" "SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute.ispc" -o "build\SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute.obj" -h "SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute.log" 2>&1

:skip_SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute

:wait_SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch
)

echo Compiling SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch.ispc... (fast)
start /b /min "ISPC_SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch" "%ISPC%" "SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch.ispc" -o "build\SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch.obj" -h "SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch.log" 2>&1

:skip_SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch

:wait_all
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GTR 0 (
    >nul timeout /t 1 /nobreak
    goto :wait_all
)

if not exist "build\SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute.obj" set FAILED=1
if exist "build\SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute.obj" if %%~z"build\SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute.obj"==0 set FAILED=1
if not exist "build\SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute.obj" set FAILED=1
if exist "build\SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute.obj" if %%~z"build\SharpNative_Job__global_namespace__MoveSystemJobEntityIspc_Execute.obj"==0 set FAILED=1
if not exist "build\SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch.obj" set FAILED=1
if exist "build\SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch.obj" if %%~z"build\SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch.obj"==0 set FAILED=1

if "%FAILED%"=="1" (
    echo One or more ISPC files failed to compile. Check .log files for details.
    exit /b 1
)
echo All ISPC files compiled successfully.
