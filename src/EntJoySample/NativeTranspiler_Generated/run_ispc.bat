@echo off
set ISPC=E:/Code/ispc-v1.30.0-windows/bin/ispc.exe
if not exist "%ISPC%" (
    echo ISPC not found at %ISPC%
    exit /b 1
)
cd /d "%~dp0"
if not exist build mkdir build
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
