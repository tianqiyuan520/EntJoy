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

:wait_SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic
)

echo Compiling SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.ispc... (fast)
start /b /min "ISPC_SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic" "%ISPC%" "SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.ispc" -o "build\SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.obj" -h "SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.log" 2>&1

:skip_SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic

:wait_SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch
)

echo Compiling SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.ispc... (fast)
start /b /min "ISPC_SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch" "%ISPC%" "SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.ispc" -o "build\SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.obj" -h "SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.log" 2>&1

:skip_SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch

:wait_SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute
)

echo Compiling SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.ispc... (fast)
start /b /min "ISPC_SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute" "%ISPC%" "SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.ispc" -o "build\SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.obj" -h "SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.log" 2>&1

:skip_SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute

:wait_SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute
)

echo Compiling SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.ispc... (fast)
start /b /min "ISPC_SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute" "%ISPC%" "SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.ispc" -o "build\SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.obj" -h "SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.log" 2>&1

:skip_SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute

:wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute
)

echo Compiling SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc... (fast)
start /b /min "ISPC_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute" "%ISPC%" "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc" -o "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.obj" -h "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.log" 2>&1

:skip_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute

:wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute
)

echo Compiling SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.ispc... (fast)
start /b /min "ISPC_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute" "%ISPC%" "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.ispc" -o "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.obj" -h "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.log" 2>&1

:skip_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute

:wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt
)

echo Compiling SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.ispc... (fast)
start /b /min "ISPC_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt" "%ISPC%" "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.ispc" -o "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.obj" -h "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.log" 2>&1

:skip_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt

:wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute
)

echo Compiling SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc... (fast)
start /b /min "ISPC_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute" "%ISPC%" "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc" -o "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.obj" -h "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.log" 2>&1

:skip_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute

:wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute
)

echo Compiling SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.ispc... (fast)
start /b /min "ISPC_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute" "%ISPC%" "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.ispc" -o "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.obj" -h "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.log" 2>&1

:skip_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute

:wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GEQ !MAXCONCURRENT! (
    >nul timeout /t 1 /nobreak
    goto :wait_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt
)

echo Compiling SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.ispc... (fast)
start /b /min "ISPC_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt" "%ISPC%" "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.ispc" -o "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.obj" -h "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma > "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.log" 2>&1

:skip_SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt

:wait_all
set RUNNING=0
for /f %%p in ('tasklist /fi "imagename eq ispc.exe" 2^>nul ^| find /c "ispc.exe"') do set RUNNING=%%p
if !RUNNING! GTR 0 (
    >nul timeout /t 1 /nobreak
    goto :wait_all
)

if not exist "build\SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.obj" set FAILED=1
if exist "build\SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.obj" if %%~z"build\SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.obj"==0 set FAILED=1
if not exist "build\SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.obj" set FAILED=1
if exist "build\SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.obj" if %%~z"build\SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.obj"==0 set FAILED=1
if not exist "build\SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.obj" set FAILED=1
if exist "build\SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.obj" if %%~z"build\SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.obj"==0 set FAILED=1
if not exist "build\SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.obj" set FAILED=1
if exist "build\SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.obj" if %%~z"build\SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.obj"==0 set FAILED=1
if not exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.obj" set FAILED=1
if exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.obj" if %%~z"build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.obj"==0 set FAILED=1
if not exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.obj" set FAILED=1
if exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.obj" if %%~z"build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.obj"==0 set FAILED=1
if not exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.obj" set FAILED=1
if exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.obj" if %%~z"build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.obj"==0 set FAILED=1
if not exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.obj" set FAILED=1
if exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.obj" if %%~z"build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.obj"==0 set FAILED=1
if not exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.obj" set FAILED=1
if exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.obj" if %%~z"build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.obj"==0 set FAILED=1
if not exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.obj" set FAILED=1
if exist "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.obj" if %%~z"build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.obj"==0 set FAILED=1

if "%FAILED%"=="1" (
    echo One or more ISPC files failed to compile. Check .log files for details.
    exit /b 1
)
echo All ISPC files compiled successfully.
