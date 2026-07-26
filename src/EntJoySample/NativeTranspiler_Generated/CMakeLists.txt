cmake_minimum_required(VERSION 3.10)
set(CMAKE_INSTALL_PREFIX "${CMAKE_CURRENT_BINARY_DIR}/install" CACHE PATH "Install prefix" FORCE)
project(NativeDll LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include_directories(${CMAKE_CURRENT_SOURCE_DIR})
include_directories("${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll")

# ============================================================
# CPU architecture detection
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

# ============================================================
# SIMD level: AUTO or user override (-DNATIVE_SIMD_LEVEL=AVX2|SSE4|NEON|SCALAR)
# ============================================================
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

# No explicit task system defined; tasksys.cpp will pick the best one for the platform
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/tasksys.cpp")
    set(TASKSYS_SRC "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/tasksys.cpp")
else()
    set(TASKSYS_SRC "")
    message(WARNING "tasksys.cpp not found at ${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/tasksys.cpp")
endif()

add_library(NativeDll SHARED
    "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/Exports.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/JobProfiler.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/JobSystem.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/NativeWorkerPool.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../NativeDll/Native.cpp"
    SharpNative_EntJoySample_AutoSIMDTest_ComplexFlow_StaticFuncs_ComplexFlow_Stc_Cpp.cpp
    SharpNative_EntJoySample_AutoSIMDTest_ComplexFlow_StaticFuncs_ComplexFlow_Stc_ISPC_wrapper.cpp
    SharpNative_EntJoySample_AutoSIMDTest_ComplexFlow_StaticFuncs_ComplexFlow_Stc_SIMD.cpp
    SharpNative_EntJoySample_AutoSIMDTest_GatherReduce_StaticFuncs_GatherReduce_Stc_Cpp.cpp
    SharpNative_EntJoySample_AutoSIMDTest_GatherReduce_StaticFuncs_GatherReduce_Stc_ISPC_wrapper.cpp
    SharpNative_EntJoySample_AutoSIMDTest_GatherReduce_StaticFuncs_GatherReduce_Stc_SIMD.cpp
    SharpNative_EntJoySample_AutoSIMDTest_MathFuncs_StaticFuncs_MathFuncs_Stc_Cpp.cpp
    SharpNative_EntJoySample_AutoSIMDTest_MathFuncs_StaticFuncs_MathFuncs_Stc_ISPC_wrapper.cpp
    SharpNative_EntJoySample_AutoSIMDTest_MathFuncs_StaticFuncs_MathFuncs_Stc_SIMD.cpp
    SharpNative_EntJoySample_AutoSIMDTest_SimpleArith_StaticFuncs_SimpleArith_Stc_Cpp.cpp
    SharpNative_EntJoySample_AutoSIMDTest_SimpleArith_StaticFuncs_SimpleArith_Stc_ISPC_wrapper.cpp
    SharpNative_EntJoySample_AutoSIMDTest_SimpleArith_StaticFuncs_SimpleArith_Stc_SIMD.cpp
    SharpNative_EntJoySample_AutoSIMDTest_SimpleReduce_StaticFuncs_SimpleReduce_Stc_Cpp.cpp
    SharpNative_EntJoySample_AutoSIMDTest_SimpleReduce_StaticFuncs_SimpleReduce_Stc_ISPC_wrapper.cpp
    SharpNative_EntJoySample_AutoSIMDTest_SimpleReduce_StaticFuncs_SimpleReduce_Stc_SIMD.cpp
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
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_Cpp_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_Cpp_For_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_Cpp_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_Cpp_Job_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_Cpp_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_Cpp_PF_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_For_Execute_Batch_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_Job_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_PF_Execute_Batch_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_SIMD_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_SIMD_For_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_SIMD_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_SIMD_Job_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_SIMD_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_SIMD_PF_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_Cpp_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_Cpp_For_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_Cpp_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_Cpp_Job_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_Cpp_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_Cpp_PF_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_For_Execute_Batch_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_Job_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_PF_Execute_Batch_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_SIMD_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_SIMD_For_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_SIMD_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_SIMD_Job_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_SIMD_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_SIMD_PF_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_Cpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_Cpp_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_ISPC_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_SIMD_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_SIMD_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobEntity_Cpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobEntity_Cpp_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobEntity_SIMD_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobEntity_SIMD_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_Cpp_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_Cpp_For_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_Cpp_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_Cpp_Job_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_Cpp_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_Cpp_PF_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_For_Execute_Batch_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_Job_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_PF_Execute_Batch_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_SIMD_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_SIMD_For_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_SIMD_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_SIMD_Job_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_SIMD_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_SIMD_PF_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_Cpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_Cpp_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_ISPC_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_SIMD_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_SIMD_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobEntity_Cpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobEntity_Cpp_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobEntity_SIMD_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobEntity_SIMD_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_Cpp_Chunk_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_Cpp_Chunk_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_Cpp_Entity_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_Cpp_Entity_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_Cpp_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_Cpp_For_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_Cpp_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_Cpp_Job_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_Cpp_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_Cpp_PF_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_For_Execute_Batch_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_Job_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_PF_Execute_Batch_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_SIMD_Entity_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_SIMD_Entity_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_SIMD_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_SIMD_For_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_SIMD_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_SIMD_Job_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_SIMD_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_SIMD_PF_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_Cpp_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_Cpp_For_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_Cpp_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_Cpp_Job_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_Cpp_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_Cpp_PF_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_For_Execute_Batch_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_Job_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_PF_Execute_Batch_wrapper.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_SIMD_For_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_SIMD_For_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_SIMD_Job_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_SIMD_Job_Execute.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_SIMD_PF_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_SIMD_PF_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCpp_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCppFast_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkCppFast_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityCpp_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCpp_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCppFast_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkCppFast_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityCpp_Execute_Adapter.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityCpp_Execute.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute_wrapper.cpp
    SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt_wrapper.cpp
    ${TASKSYS_SRC}
)

# ============================================================
# SIMD arch flags + defines
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

# SIMD math precision: 1=Fastest(~3.5ULP) 2=High(~1.0ULP) 3=IEEE(exact)
if(NOT DEFINED NATIVE_SIMD_MATH_PRECISION)
    set(NATIVE_SIMD_MATH_PRECISION "1" CACHE STRING "SIMD math precision level: 1=Fastest 2=High 3=IEEE")
endif()
target_compile_definitions(NativeDll PRIVATE SIMD_MATH_PRECISION=${NATIVE_SIMD_MATH_PRECISION})

# ============================================================
# ISPC: x86 only, optional
# ============================================================
find_program(ISPC_EXECUTABLE ispc)
if(ISPC_EXECUTABLE AND NATIVE_ARCH MATCHES "x86_64|x86")
    set(HAS_ISPC TRUE)
    message(STATUS "NativeDll: ISPC found")
else()
    set(HAS_ISPC FALSE)
endif()

if(HAS_ISPC)
    set(ISPC_OBJECTS
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleReduce_StaticFuncs_SimpleReduce_Stc_ISPC.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_MathFuncs_StaticFuncs_MathFuncs_Stc_ISPC.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_ComplexFlow_StaticFuncs_ComplexFlow_Stc_ISPC.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_GatherReduce_StaticFuncs_GatherReduce_Stc_ISPC.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleArith_StaticFuncs_SimpleArith_Stc_ISPC.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_ISPC_Execute.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_ISPC_Execute.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_Job_Execute.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_For_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_PF_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_Job_Execute.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_For_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_PF_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_Job_Execute.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_For_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_PF_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_Job_Execute.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_For_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_PF_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_Job_Execute.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_For_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_PF_Execute_Batch.obj"
    )

    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleReduce_StaticFuncs_SimpleReduce_Stc_ISPC.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleReduce_StaticFuncs_SimpleReduce_Stc_ISPC_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleReduce_StaticFuncs_SimpleReduce_Stc_ISPC.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleReduce_StaticFuncs_SimpleReduce_Stc_ISPC.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleReduce_StaticFuncs_SimpleReduce_Stc_ISPC_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleReduce_StaticFuncs_SimpleReduce_Stc_ISPC.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_EntJoySample_AutoSIMDTest_SimpleReduce_StaticFuncs_SimpleReduce_Stc_ISPC.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_MathFuncs_StaticFuncs_MathFuncs_Stc_ISPC.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_MathFuncs_StaticFuncs_MathFuncs_Stc_ISPC_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_MathFuncs_StaticFuncs_MathFuncs_Stc_ISPC.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_MathFuncs_StaticFuncs_MathFuncs_Stc_ISPC.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_MathFuncs_StaticFuncs_MathFuncs_Stc_ISPC_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_MathFuncs_StaticFuncs_MathFuncs_Stc_ISPC.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_EntJoySample_AutoSIMDTest_MathFuncs_StaticFuncs_MathFuncs_Stc_ISPC.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_ComplexFlow_StaticFuncs_ComplexFlow_Stc_ISPC.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_ComplexFlow_StaticFuncs_ComplexFlow_Stc_ISPC_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_ComplexFlow_StaticFuncs_ComplexFlow_Stc_ISPC.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_ComplexFlow_StaticFuncs_ComplexFlow_Stc_ISPC.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_ComplexFlow_StaticFuncs_ComplexFlow_Stc_ISPC_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_ComplexFlow_StaticFuncs_ComplexFlow_Stc_ISPC.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_EntJoySample_AutoSIMDTest_ComplexFlow_StaticFuncs_ComplexFlow_Stc_ISPC.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_GatherReduce_StaticFuncs_GatherReduce_Stc_ISPC.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_GatherReduce_StaticFuncs_GatherReduce_Stc_ISPC_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_GatherReduce_StaticFuncs_GatherReduce_Stc_ISPC.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_GatherReduce_StaticFuncs_GatherReduce_Stc_ISPC.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_GatherReduce_StaticFuncs_GatherReduce_Stc_ISPC_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_GatherReduce_StaticFuncs_GatherReduce_Stc_ISPC.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_EntJoySample_AutoSIMDTest_GatherReduce_StaticFuncs_GatherReduce_Stc_ISPC.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleArith_StaticFuncs_SimpleArith_Stc_ISPC.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleArith_StaticFuncs_SimpleArith_Stc_ISPC_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleArith_StaticFuncs_SimpleArith_Stc_ISPC.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleArith_StaticFuncs_SimpleArith_Stc_ISPC.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleArith_StaticFuncs_SimpleArith_Stc_ISPC_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_EntJoySample_AutoSIMDTest_SimpleArith_StaticFuncs_SimpleArith_Stc_ISPC.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_EntJoySample_AutoSIMDTest_SimpleArith_StaticFuncs_SimpleArith_Stc_ISPC.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_ISPC_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_ISPC_Execute_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_ISPC_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_ISPC_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_ISPC_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_ISPC_Execute.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_MoveJobChunk_ISPC_Execute.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_ISPC_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_ISPC_Execute_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_ISPC_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_ISPC_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_ISPC_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_ISPC_Execute.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_HeavyJobChunk_ISPC_Execute.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_Job_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_Job_Execute_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_Job_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_Job_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_Job_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_Job_Execute.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_Job_Execute.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_For_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_For_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_For_Execute_Batch.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_For_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_For_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_For_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_For_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_PF_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_PF_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_PF_Execute_Batch.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_PF_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_PF_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_PF_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleReduce_ISPC_PF_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_Job_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_Job_Execute_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_Job_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_Job_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_Job_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_Job_Execute.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_Job_Execute.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_For_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_For_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_For_Execute_Batch.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_For_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_For_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_For_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_For_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_PF_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_PF_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_PF_Execute_Batch.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_PF_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_PF_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_PF_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_MathFuncs_ISPC_PF_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_Job_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_Job_Execute_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_Job_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_Job_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_Job_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_Job_Execute.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_Job_Execute.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_For_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_For_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_For_Execute_Batch.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_For_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_For_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_For_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_For_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_PF_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_PF_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_PF_Execute_Batch.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_PF_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_PF_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_PF_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_ComplexFlow_ISPC_PF_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobChunkIspc_Execute.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspc_Execute.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_MoveJobEntityIspcMt_Execute_mt.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobChunkIspc_Execute.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspc_Execute.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_IJobChunkMoveCompareTest_HeavyJobEntityIspcMt_Execute_mt.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_Job_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_Job_Execute_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_Job_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_Job_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_Job_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_Job_Execute.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_Job_Execute.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_For_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_For_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_For_Execute_Batch.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_For_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_For_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_For_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_For_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_PF_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_PF_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_PF_Execute_Batch.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_PF_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_PF_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_PF_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_GatherReduce_ISPC_PF_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_Job_Execute.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_Job_Execute_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_Job_Execute.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_Job_Execute.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_Job_Execute_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_Job_Execute.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_Job_Execute.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_For_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_For_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_For_Execute_Batch.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_For_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_For_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_For_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_For_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_PF_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_PF_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_PF_Execute_Batch.ispc" -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_PF_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_PF_Execute_Batch_ispc.h" --target=avx512skx-i32x16 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_PF_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_EntJoySample_AutoSIMDTest_SimpleArith_ISPC_PF_Execute_Batch.ispc"
        VERBATIM
    )
    set_source_files_properties(${ISPC_OBJECTS} PROPERTIES EXTERNAL_OBJECT TRUE GENERATED TRUE)
    target_sources(NativeDll PRIVATE ${ISPC_OBJECTS})

    if(TASKSYS_SRC)
        set_source_files_properties(${TASKSYS_SRC} PROPERTIES COMPILE_FLAGS "/arch:AVX")
    endif()
endif()

# ============================================================
# Global compiler flags
# ============================================================
if(MSVC)
    target_compile_options(NativeDll PRIVATE /std:c++20 /O2 /Ob2 /Oi /Ot /Qpar /MP /fp:fast)
    target_compile_definitions(NativeDll PRIVATE NDEBUG NOMINMAX NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)
else()
    target_compile_options(NativeDll PRIVATE -O3 -march=native -mtune=native -ffast-math -ffp-contract=fast -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer)
    target_compile_definitions(NativeDll PRIVATE NDEBUG NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)
endif()

# ============================================================
# Platform-specific output suffix
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
