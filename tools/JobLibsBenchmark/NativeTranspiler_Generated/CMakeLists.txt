cmake_minimum_required(VERSION 3.10)
set(CMAKE_INSTALL_PREFIX "${CMAKE_CURRENT_BINARY_DIR}/install" CACHE PATH "Install prefix" FORCE)
project(NativeDll LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(CMAKE_UNITY_BUILD ON)
set(CMAKE_UNITY_BUILD_BATCH_SIZE 0)
add_definitions(-DIMGUI_DEFINE_MATH_OPERATORS)

include_directories(${CMAKE_CURRENT_SOURCE_DIR})
include_directories("${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll")

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
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/tasksys.cpp")
    set(TASKSYS_SRC "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/tasksys.cpp")
else()
    set(TASKSYS_SRC "")
    message(WARNING "tasksys.cpp not found at ${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/tasksys.cpp")
endif()

# Core runtime (NativeDll.dll)
add_library(NativeDll SHARED
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/ChaseLevScheduler.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/Exports.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/JobDebuggerGUI.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/JobProfiler.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/JobSystem.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/JobSystem_Scheduler.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/JobSystem_State.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/JobSystem_Tiles.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/Native.cpp"
    "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/NativeWorkerPool.cpp"
)

# Generated job wrappers + ISPC runtime (NativeTranspiled.dll)
#   tasksys.cpp（ISPCAlloc/ISPCLaunch/ISPCSync 任务系统）必须与 ISPC 编译产物
#   同 DLL：ISPC .obj 以普通符号引用 ISPCLaunch 等，MSVC 无法从另一 DLL
#   自动导入普通符号（非 __imp_），故 tasksys + ISPC objects + 生成代码要同库。
add_library(NativeTranspiled SHARED
    SharpNative_Job_JobLibsBenchmark_NativeAddAutoSIMD_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeAddAutoSIMD_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeAddCpp_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeAddCpp_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeAddIspc_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeAddIspc_Execute_Batch_wrapper.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainAutoSIMD1_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainAutoSIMD1_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainAutoSIMD2_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainAutoSIMD2_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainAutoSIMD3_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainAutoSIMD3_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainCpp1_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainCpp1_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainCpp2_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainCpp2_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainCpp3_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainCpp3_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainIspc1_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainIspc1_Execute_Batch_wrapper.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainIspc2_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainIspc2_Execute_Batch_wrapper.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainIspc3_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeChainIspc3_Execute_Batch_wrapper.cpp
    SharpNative_Job_JobLibsBenchmark_NativeCtrlAutoSIMD_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeCtrlAutoSIMD_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeCtrlCpp_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeCtrlCpp_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeCtrlIspc_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeCtrlIspc_Execute_Batch_wrapper.cpp
    SharpNative_Job_JobLibsBenchmark_NativeEmptyCpp_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeEmptyCpp_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeEmptyIspc_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeEmptyIspc_Execute_Batch_wrapper.cpp
    SharpNative_Job_JobLibsBenchmark_NativeHeavyAutoSIMD_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeHeavyAutoSIMD_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeHeavyCpp_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeHeavyCpp_Execute.cpp
    SharpNative_Job_JobLibsBenchmark_NativeHeavyIspc_Execute_Adapter.cpp
    SharpNative_Job_JobLibsBenchmark_NativeHeavyIspc_Execute_Batch_wrapper.cpp
    ${TASKSYS_SRC}
)
target_link_libraries(NativeTranspiled PRIVATE NativeDll)

# ============================================================
# Dear ImGui debug panel (Windows / D3D11)
# ============================================================
if(WIN32)
    set(IMGUI_DIR   "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll/thirdParty/imgui")
    set(IMGUI_BACK  "${IMGUI_DIR}/backends")
    target_include_directories(NativeDll PRIVATE ${IMGUI_DIR} ${IMGUI_BACK})
    target_sources(NativeDll PRIVATE
        ${IMGUI_DIR}/imgui.cpp
        ${IMGUI_DIR}/imgui_draw.cpp
        ${IMGUI_DIR}/imgui_tables.cpp
        ${IMGUI_DIR}/imgui_widgets.cpp
        ${IMGUI_BACK}/imgui_impl_win32.cpp
        ${IMGUI_BACK}/imgui_impl_dx11.cpp
    )
    target_link_libraries(NativeDll PRIVATE d3d11 dxgi)
    target_compile_definitions(NativeDll PRIVATE ENTJOY_IMGUI_ENABLED=1)
endif()

# ============================================================
# SIMD arch flags + defines（NativeDll + NativeTranspiled 一致）
# ============================================================
foreach(SIMD_TGT NativeDll NativeTranspiled)
    if(NATIVE_SIMD_LEVEL STREQUAL "AVX2")
        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_AVX2 NSIMD_WIDTH=8)
        if(MSVC)
            target_compile_options(${SIMD_TGT} PRIVATE /arch:AVX2)
        else()
            target_compile_options(${SIMD_TGT} PRIVATE -mavx2 -mbmi2 -mfma)
        endif()
    elseif(NATIVE_SIMD_LEVEL STREQUAL "AVX")
        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_AVX NSIMD_WIDTH=8)
        if(MSVC)
            target_compile_options(${SIMD_TGT} PRIVATE /arch:AVX)
        else()
            target_compile_options(${SIMD_TGT} PRIVATE -mavx)
        endif()
    elseif(NATIVE_SIMD_LEVEL STREQUAL "SSE4")
        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_SSE4 NSIMD_WIDTH=4)
        if(NOT MSVC)
            target_compile_options(${SIMD_TGT} PRIVATE -msse4.2)
        endif()
    elseif(NATIVE_SIMD_LEVEL STREQUAL "NEON")
        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_NEON NSIMD_WIDTH=4)
        if(NOT MSVC)
            target_compile_options(${SIMD_TGT} PRIVATE -march=armv8-a+simd)
        endif()
    else()
        target_compile_definitions(${SIMD_TGT} PRIVATE NSIMD_SCALAR NSIMD_WIDTH=1)
    endif()
    # SIMD math precision: 1=Fastest(~3.5ULP) 2=High(~1.0ULP) 3=IEEE(exact)
    if(NOT DEFINED NATIVE_SIMD_MATH_PRECISION)
        set(NATIVE_SIMD_MATH_PRECISION "1" CACHE STRING "SIMD math precision level: 1=Fastest 2=High 3=IEEE")
    endif()
    target_compile_definitions(${SIMD_TGT} PRIVATE SIMD_MATH_PRECISION=${NATIVE_SIMD_MATH_PRECISION})
endforeach()

# Sentinel layout: match C# #if DEBUG DisposeSentinel (native templates add 8B sentinel field)
option(ENTJOY_ENABLE_SENTINEL "Match C# #if DEBUG DisposeSentinel container layout" OFF)
if(ENTJOY_ENABLE_SENTINEL)
    target_compile_definitions(NativeDll PRIVATE ENTJOY_ENABLE_SENTINEL)
    target_compile_definitions(NativeTranspiled PRIVATE ENTJOY_ENABLE_SENTINEL)
    message(STATUS "NativeDll/NativeTranspiled: ENTJOY_ENABLE_SENTINEL ON (NativeArray=40B / NativeList=32B)")
endif()

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
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeAddIspc_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeEmptyIspc_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc1_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc2_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc3_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeHeavyIspc_Execute_Batch.obj"
        "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeCtrlIspc_Execute_Batch.obj"
    )

    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeAddIspc_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeAddIspc_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeAddIspc_Execute_Batch.ispc" -O3 -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeAddIspc_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeAddIspc_Execute_Batch_ispc.h" --target=avx2-i32x8 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeAddIspc_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_JobLibsBenchmark_NativeAddIspc_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeEmptyIspc_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeEmptyIspc_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeEmptyIspc_Execute_Batch.ispc" -O3 -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeEmptyIspc_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeEmptyIspc_Execute_Batch_ispc.h" --target=avx2-i32x8 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeEmptyIspc_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_JobLibsBenchmark_NativeEmptyIspc_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc1_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc1_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc1_Execute_Batch.ispc" -O3 -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc1_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc1_Execute_Batch_ispc.h" --target=avx2-i32x8 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc1_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_JobLibsBenchmark_NativeChainIspc1_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc2_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc2_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc2_Execute_Batch.ispc" -O3 -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc2_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc2_Execute_Batch_ispc.h" --target=avx2-i32x8 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc2_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_JobLibsBenchmark_NativeChainIspc2_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc3_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc3_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc3_Execute_Batch.ispc" -O3 -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc3_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc3_Execute_Batch_ispc.h" --target=avx2-i32x8 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeChainIspc3_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_JobLibsBenchmark_NativeChainIspc3_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeHeavyIspc_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeHeavyIspc_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeHeavyIspc_Execute_Batch.ispc" -O3 -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeHeavyIspc_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeHeavyIspc_Execute_Batch_ispc.h" --target=avx2-i32x8 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeHeavyIspc_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_JobLibsBenchmark_NativeHeavyIspc_Execute_Batch.ispc"
        VERBATIM
    )
    add_custom_command(
        OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeCtrlIspc_Execute_Batch.obj" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeCtrlIspc_Execute_Batch_ispc.h"
        COMMAND "${ISPC_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeCtrlIspc_Execute_Batch.ispc" -O3 -o "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job_JobLibsBenchmark_NativeCtrlIspc_Execute_Batch.obj" -h "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeCtrlIspc_Execute_Batch_ispc.h" --target=avx2-i32x8 --math-lib=fast
        DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/SharpNative_Job_JobLibsBenchmark_NativeCtrlIspc_Execute_Batch.ispc"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Compiling ISPC SharpNative_Job_JobLibsBenchmark_NativeCtrlIspc_Execute_Batch.ispc"
        VERBATIM
    )
    set_source_files_properties(${ISPC_OBJECTS} PROPERTIES EXTERNAL_OBJECT TRUE GENERATED TRUE)
    target_sources(NativeTranspiled PRIVATE ${ISPC_OBJECTS})

    if(TASKSYS_SRC)
        set_source_files_properties(${TASKSYS_SRC} PROPERTIES COMPILE_FLAGS "/arch:AVX")
    endif()
endif()

# ============================================================
# Global compiler flags
# ============================================================
if(MSVC)
    # 源文件为 UTF-8 无 BOM（含中文注释）。不加 /utf-8 时 MSVC 按本地代码页
    # （中文系统=936/GBK）读取：UTF-8 汉字字节序列在 GBK 下可能被误判为含
    # 0x5C 反斜杠 → 注释内触发行拼接吃掉下一行 → C4819 + C2065/C2447 级联解析错误。
    if(CMAKE_CXX_COMPILER_ID STREQUAL "Clang")
        # ClangCL (LLVM backend — faster SIMD than MSVC)
        target_compile_options(NativeDll PRIVATE /utf-8 /std:c++20 /O2 /Oi /fp:fast)
        target_compile_options(NativeTranspiled PRIVATE /utf-8 /std:c++20 /O2 /Oi /fp:fast)
    else()
        # MSVC (default)
        target_compile_options(NativeDll PRIVATE /utf-8 /std:c++20 /O2 /Ob2 /Oi /Ot /Qpar /MP /fp:fast)
        target_compile_options(NativeTranspiled PRIVATE /utf-8 /std:c++20 /O2 /Ob2 /Oi /Ot /Qpar /MP /fp:fast)
    endif()
    target_compile_definitions(NativeDll PRIVATE NDEBUG NOMINMAX NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)
    # NativeTranspiled 导出生成代码 wrapper/adapter（GENERATED_API → dllexport）
    target_compile_definitions(NativeTranspiled PRIVATE NDEBUG NOMINMAX GENERATED_EXPORTS)
else()
    target_compile_options(NativeDll PRIVATE -O3 -march=native -mtune=native -ffast-math -ffp-contract=fast -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer)
    target_compile_options(NativeTranspiled PRIVATE -O3 -march=native -mtune=native -ffast-math -ffp-contract=fast -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer)
    target_compile_definitions(NativeDll PRIVATE NDEBUG NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)
    target_compile_definitions(NativeTranspiled PRIVATE NDEBUG GENERATED_EXPORTS)
endif()

# ============================================================
# Platform-specific output suffix
# ============================================================
if(WIN32)
    set_target_properties(NativeDll PROPERTIES SUFFIX ".dll")
    set_target_properties(NativeTranspiled PROPERTIES SUFFIX ".dll")
elseif(APPLE)
    set_target_properties(NativeDll PROPERTIES SUFFIX ".dylib")
    set_target_properties(NativeTranspiled PROPERTIES SUFFIX ".dylib")
else()
    set_target_properties(NativeDll PROPERTIES SUFFIX ".so")
    set_target_properties(NativeTranspiled PROPERTIES SUFFIX ".so")
endif()

set_target_properties(NativeDll PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
)
set_target_properties(NativeTranspiled PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
)
