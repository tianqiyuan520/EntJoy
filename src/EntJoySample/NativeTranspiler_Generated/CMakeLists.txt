cmake_minimum_required(VERSION 3.10)
project(NativeDll LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include_directories(${CMAKE_CURRENT_SOURCE_DIR})
include_directories("D:/Godot/Project/EntJoy/src/NativeDll")

# No explicit task system defined; tasksys.cpp will pick the best one for the platform
if(EXISTS "D:/Godot/Project/EntJoy/src/NativeDll/tasksys.cpp")
    set(TASKSYS_SRC "D:/Godot/Project/EntJoy/src/NativeDll/tasksys.cpp")
else()
    set(TASKSYS_SRC "")
    message(WARNING "tasksys.cpp not found at D:/Godot/Project/EntJoy/src/NativeDll/tasksys.cpp")
endif()

add_library(NativeDll SHARED
    "D:/Godot/Project/EntJoy/src/NativeDll/Exports.cpp"
    "D:/Godot/Project/EntJoy/src/NativeDll/JobProfiler.cpp"
    "D:/Godot/Project/EntJoy/src/NativeDll/JobSystem.cpp"
    "D:/Godot/Project/EntJoy/src/NativeDll/Native.cpp"
    generated_unity_0.cpp
    SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic_wrapper.cpp
    SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Adapter.cpp
    SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCppFast_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCppFast_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_SimpleIJobChunkTest_AddOneJob_Execute_Adapter.cpp
    ${TASKSYS_SRC}
)

if(TASKSYS_SRC)
    set_source_files_properties(${TASKSYS_SRC} PROPERTIES COMPILE_FLAGS "/arch:AVX")
endif()

find_program(ISPC_EXECUTABLE ispc)
if(NOT ISPC_EXECUTABLE)
    set(ISPC_EXECUTABLE "E:/Code/ispc-v1.30.0-windows/bin/ispc.exe")
endif()

set(ISPC_OBJECTS
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.obj"
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.obj"
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.obj"
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.obj"
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.obj"
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.obj"
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.obj"
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.obj"
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.obj"
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.obj"
)

add_custom_command(
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic_ispc.h"
    COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.ispc"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Compiling ISPC SharpNative_EntJoy_MovementTest_MoveEntitiesTest_RunNativeIspcStatic.ispc"
    VERBATIM
)
add_custom_command(
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch_ispc.h"
    COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.ispc"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Compiling ISPC SharpNative_Job_EntJoy_MovementTest_MoveEntitiesJob_NativeIspc_Execute_Batch.ispc"
    VERBATIM
)
add_custom_command(
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute_ispc.h"
    COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.ispc"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_EmptyChunkJobIspc_Execute.ispc"
    VERBATIM
)
add_custom_command(
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute_ispc.h"
    COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.ispc"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkScheduleOverheadTest_AddOneChunkJobIspc_Execute.ispc"
    VERBATIM
)
add_custom_command(
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute_ispc.h"
    COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc"
    VERBATIM
)
add_custom_command(
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute_ispc.h"
    COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.ispc"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.ispc"
    VERBATIM
)
add_custom_command(
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt_ispc.h"
    COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.ispc"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.ispc"
    VERBATIM
)
add_custom_command(
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute_ispc.h"
    COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc"
    VERBATIM
)
add_custom_command(
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute_ispc.h"
    COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.ispc"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.ispc"
    VERBATIM
)
add_custom_command(
    OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_ispc.h"
    COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_ispc.h" --target=avx512skx-i32x16 --math-lib=fast --opt=disable-fma
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.ispc"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.ispc"
    VERBATIM
)
set_source_files_properties(${ISPC_OBJECTS} PROPERTIES EXTERNAL_OBJECT TRUE GENERATED TRUE)
target_sources(NativeDll PRIVATE ${ISPC_OBJECTS})

if(MSVC)
    target_compile_options(NativeDll PRIVATE /std:c++20 /O2 /Ob2 /Oi /Ot /GL /arch:AVX2 /Qpar)
    target_compile_definitions(NativeDll PRIVATE NDEBUG NOMINMAX NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)
    set_source_files_properties(SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCppFast_Execute.cpp PROPERTIES COMPILE_FLAGS "/fp:fast")
    set_source_files_properties(SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCppFast_Execute.cpp PROPERTIES COMPILE_FLAGS "/fp:fast")
    set_target_properties(NativeDll PROPERTIES INTERPROCEDURAL_OPTIMIZATION TRUE)
else()
    target_compile_options(NativeDll PRIVATE -O3 -march=native -mtune=native -ffast-math -ffp-contract=fast -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer)
    target_compile_definitions(NativeDll PRIVATE NDEBUG NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)
endif()

set_target_properties(NativeDll PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
)

foreach(OUTPUTCONFIG ${CMAKE_CONFIGURATION_TYPES})
    string(TOUPPER ${OUTPUTCONFIG} OUTPUTCONFIG)
    set_target_properties(NativeDll PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY_${OUTPUTCONFIG} "${CMAKE_CURRENT_BINARY_DIR}/${OUTPUTCONFIG}"
        LIBRARY_OUTPUT_DIRECTORY_${OUTPUTCONFIG} "${CMAKE_CURRENT_BINARY_DIR}/${OUTPUTCONFIG}"
        ARCHIVE_OUTPUT_DIRECTORY_${OUTPUTCONFIG} "${CMAKE_CURRENT_BINARY_DIR}/${OUTPUTCONFIG}"
    )
endforeach()

if(WIN32)
    set_target_properties(NativeDll PROPERTIES SUFFIX ".dll")
endif()
