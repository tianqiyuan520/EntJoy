@echo off
set ISPC=E:/Code/ispc-v1.30.0-windows/bin/ispc.exe
if not exist "%ISPC%" (
    echo ISPC not found at %ISPC%
    exit /b 1
)
cd /d "%~dp0"
if not exist build mkdir build
echo Compiling SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.ispc...
"%ISPC%" "SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.ispc" -o "build\SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.obj" -h "SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
if errorlevel 1 (
    echo Failed to compile SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.ispc
    exit /b 1
)
echo Compiling SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc...
"%ISPC%" "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc" -o "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.obj" -h "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
if errorlevel 1 (
    echo Failed to compile SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc
    exit /b 1
)
echo Compiling SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc...
"%ISPC%" "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc" -o "build\SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.obj" -h "SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
if errorlevel 1 (
    echo Failed to compile SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc
    exit /b 1
)
echo Compiling SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.ispc...
"%ISPC%" "SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.ispc" -o "build\SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.obj" -h "SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
if errorlevel 1 (
    echo Failed to compile SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.ispc
    exit /b 1
)
echo Compiling SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Batch.ispc...
"%ISPC%" "SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Batch.ispc" -o "build\SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Batch.obj" -h "SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
if errorlevel 1 (
    echo Failed to compile SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Batch.ispc
    exit /b 1
)
echo Compiling SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Batch.ispc...
"%ISPC%" "SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Batch.ispc" -o "build\SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Batch.obj" -h "SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
if errorlevel 1 (
    echo Failed to compile SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Batch.ispc
    exit /b 1
)
echo Compiling SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch.ispc...
"%ISPC%" "SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch.ispc" -o "build\SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch.obj" -h "SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
if errorlevel 1 (
    echo Failed to compile SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch.ispc
    exit /b 1
)
echo All ISPC files compiled successfully.
