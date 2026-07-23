cmake_minimum_required(VERSION 3.10)
project(NativeDll LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include_directories(${CMAKE_CURRENT_SOURCE_DIR})
include_directories("${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll")

# ============================================================
# CPU architecture → SIMD level
# ============================================================
if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|amd64|AMD64|x64)$")
    set(NATIVE_ARCH "x86_64")
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(aarch64|arm64|ARM64|AARCH64)$")
    set(NATIVE_ARCH "arm64")
elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(armv[7-9]|arm|ARM)$")
    set(NATIVE_ARCH "arm")
else()
    set(NATIVE_ARCH "unknown")
endif()

if(NOT DEFINED NATIVE_SIMD_LEVEL)
    set(NATIVE_SIMD_LEVEL "AUTO" CACHE STRING "SIMD: AUTO/AVX2/AVX/SSE4/NEON/SCALAR")
endif()

if(NATIVE_SIMD_LEVEL STREQUAL "AUTO")
    if(NATIVE_ARCH MATCHES "x86_64|x86")
        set(NATIVE_SIMD_LEVEL "AVX2")
    elseif(NATIVE_ARCH MATCHES "arm64|arm")
        set(NATIVE_SIMD_LEVEL "NEON")
    else()
        set(NATIVE_SIMD_LEVEL "SCALAR")
    endif()
endif()

message(STATUS "NativeDll: arch=${NATIVE_ARCH}, SIMD=${NATIVE_SIMD_LEVEL}")
message(STATUS "Override: -DNATIVE_SIMD_LEVEL=AVX2|AVX|SSE4|NEON|SCALAR")

# ============================================================
# ISPC: x86 only, optional
# ============================================================
find_program(ISPC_EXECUTABLE ispc)
if(ISPC_EXECUTABLE AND NATIVE_ARCH MATCHES "x86_64|x86")
    set(HAS_ISPC TRUE)
    message(STATUS "NativeDll: ISPC found (${ISPC_EXECUTABLE})")
else()
    set(HAS_ISPC FALSE)
    if(NATIVE_ARCH MATCHES "x86_64|x86")
        message(STATUS "NativeDll: ISPC not found — skipping ISPC targets")
    endif()
endif()

# ============================================================
# Sources
# ============================================================
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/tasksys.cpp")
    set(TASKSYS_SRC "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/tasksys.cpp")
else()
    set(TASKSYS_SRC "")
endif()

add_library(NativeDll SHARED
    "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/Exports.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/JobProfiler.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/JobSystem.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/NativeWorkerPool.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/Native.cpp"
    SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute_Adapter.cpp
    SharpNative_Job__global_namespace__GridSearch2D_AssignAndCountJobPointer_Execute.cpp
    SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Adapter.cpp
    SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute.cpp
    SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute_Adapter.cpp
    SharpNative_Job__global_namespace__GridSearch2D_CopyHashIndexJobPointer_Execute.cpp
    SharpNative_Job__global_namespace__GridSearch2D_FillCellStartEndJobPointer_Execute_Adapter.cpp
    SharpNative_Job__global_namespace__GridSearch2D_FillCellStartEndJobPointer_Execute.cpp
    SharpNative_Job__global_namespace__GridSearch2D_FindWithinJobPointer_Execute_Adapter.cpp
    SharpNative_Job__global_namespace__GridSearch2D_FindWithinJobPointer_Execute.cpp
    SharpNative_Job__global_namespace__GridSearch2D_GridInitializationJobPointer_Execute_Adapter.cpp
    SharpNative_Job__global_namespace__GridSearch2D_GridInitializationJobPointer_Execute.cpp
    SharpNative_Job__global_namespace__GridSearch2D_PlaceElementsJobPointer_Execute_Adapter.cpp
    SharpNative_Job__global_namespace__GridSearch2D_PlaceElementsJobPointer_Execute.cpp
    SharpNative_Job__global_namespace__GridSearch2D_PrefixSumJobPointer_Execute_Adapter.cpp
    SharpNative_Job__global_namespace__GridSearch2D_PrefixSumJobPointer_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCppFast_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCppFast_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityCpp_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCpp_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCppFast_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCppFast_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityCpp_Execute.cpp
    ${TASKSYS_SRC}
)

# ============================================================
# SIMD configuration
# ============================================================
if(NATIVE_SIMD_LEVEL STREQUAL "AVX2")
    target_compile_definitions(NativeDll PRIVATE NSIMD_AVX2 NSIMD_WIDTH=8)
    if(MSVC)
        target_compile_options(NativeDll PRIVATE /arch:AVX2)
    else()
        target_compile_options(NativeDll PRIVATE -mavx2 -mbmi2 -mfma)
    endif()
elseif(NATIVE_SIMD_LEVEL STREQUAL "AVX")
    target_compile_definitions(NativeDll PRIVATE NSIMD_AVX NSIMD_WIDTH=8)
    if(MSVC)
        target_compile_options(NativeDll PRIVATE /arch:AVX)
    else()
        target_compile_options(NativeDll PRIVATE -mavx)
    endif()
elseif(NATIVE_SIMD_LEVEL STREQUAL "SSE4")
    target_compile_definitions(NativeDll PRIVATE NSIMD_SSE4 NSIMD_WIDTH=4)
    if(NOT MSVC)
        target_compile_options(NativeDll PRIVATE -msse4.2)
    endif()
elseif(NATIVE_SIMD_LEVEL STREQUAL "NEON")
    target_compile_definitions(NativeDll PRIVATE NSIMD_NEON NSIMD_WIDTH=4)
    if(NOT MSVC)
        target_compile_options(NativeDll PRIVATE -march=armv8-a+simd)
    endif()
else()
    target_compile_definitions(NativeDll PRIVATE NSIMD_SCALAR NSIMD_WIDTH=1)
endif()

# ============================================================
# Global flags
# ============================================================
if(MSVC)
    target_compile_options(NativeDll PRIVATE /O2 /Ob2 /Oi /Ot /Qpar /MP)
    target_compile_definitions(NativeDll PRIVATE NDEBUG NOMINMAX NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)
    set_source_files_properties(
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCppFast_Execute.cpp
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCppFast_Execute.cpp
        PROPERTIES COMPILE_FLAGS "/fp:fast"
    )
    if(HAS_ISPC AND TASKSYS_SRC)
        set_source_files_properties(${TASKSYS_SRC} PROPERTIES COMPILE_FLAGS "/arch:AVX")
    endif()
else()
    target_compile_options(NativeDll PRIVATE
        -Ofast -ffast-math -ffp-contract=fast
        -fno-signed-zeros -fno-trapping-math
        -funroll-loops -fstrict-aliasing -fomit-frame-pointer
    )
    target_compile_definitions(NativeDll PRIVATE NDEBUG NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)
endif()

# ============================================================
# ISPC compilation
# ============================================================
if(HAS_ISPC)
    target_sources(NativeDll PRIVATE
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute_wrapper.cpp
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute_wrapper.cpp
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt_wrapper.cpp
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute_wrapper.cpp
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute_wrapper.cpp
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_wrapper.cpp
    )

    function(add_ispc_target name ispc_file header)
        set(obj "${CMAKE_CURRENT_BINARY_DIR}/${name}.obj")
        add_custom_command(
            OUTPUT "${obj}" "${CMAKE_CURRENT_SOURCE_DIR}/${header}"
            COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/${ispc_file}"
                    -o "${obj}" -h "${CMAKE_CURRENT_SOURCE_DIR}/${header}"
                    --target=avx512skx-i32x16 --math-lib=fast
            DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/${ispc_file}"
            WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
            COMMENT "ISPC ${ispc_file}" VERBATIM
        )
        set_source_files_properties("${obj}" PROPERTIES EXTERNAL_OBJECT TRUE GENERATED TRUE)
        target_sources(NativeDll PRIVATE "${obj}")
    endfunction()

    add_ispc_target(SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute_ispc.h)
    add_ispc_target(SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.ispc
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute_ispc.h)
    add_ispc_target(SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.ispc
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt_ispc.h)
    add_ispc_target(SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute_ispc.h)
    add_ispc_target(SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.ispc
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute_ispc.h)
    add_ispc_target(SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.ispc
        SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_ispc.h)
endif()

# ============================================================
# Output
# ============================================================
if(WIN32)
    set_target_properties(NativeDll PROPERTIES SUFFIX ".dll")
elseif(APPLE)
    set_target_properties(NativeDll PROPERTIES SUFFIX ".dylib")
else()
    set_target_properties(NativeDll PROPERTIES SUFFIX ".so")
endif()

set_target_properties(NativeDll PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
)
