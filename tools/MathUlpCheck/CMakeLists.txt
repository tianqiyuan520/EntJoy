cmake_minimum_required(VERSION 3.20)
project(MathUlpCheck CXX)

# 量内联数学实现的误差。ENTJOY_MATH_ISA 选择目标指令集：avx2（默认）/ avx512 / neon。
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT DEFINED ENTJOY_NATIVE_DLL_DIR)
    set(ENTJOY_NATIVE_DLL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../src/NativeDll")
endif()

set(ENTJOY_MATH_ISA "avx2" CACHE STRING "目标指令集：avx2 | avx512 | neon")

if(ENTJOY_MATH_ISA STREQUAL "avx512")
    set(ENTJOY_SIMD_DEFS NSIMD_AVX512 NSIMD_WIDTH=16)
    if(MSVC)
        set(ENTJOY_SIMD_FLAGS /arch:AVX512)
    else()
        set(ENTJOY_SIMD_FLAGS -mavx512f -mavx2)
    endif()
elseif(ENTJOY_MATH_ISA STREQUAL "neon")
    set(ENTJOY_SIMD_DEFS NSIMD_NEON NSIMD_WIDTH=4)
    set(ENTJOY_SIMD_FLAGS "")
else()
    set(ENTJOY_SIMD_DEFS NSIMD_AVX2 NSIMD_WIDTH=8)
    if(MSVC)
        set(ENTJOY_SIMD_FLAGS /arch:AVX2)
    else()
        set(ENTJOY_SIMD_FLAGS -mavx2)
    endif()
endif()

if(MSVC)
    set(ENTJOY_BASE_FLAGS /O2 /fp:precise)
else()
    set(ENTJOY_BASE_FLAGS -O2 -ffp-contract=off)
endif()

# 所有目标共用一套宏与编译选项，只有精度档不同
function(entjoy_math_target name source precision)
    add_executable(${name} ${source})
    target_compile_definitions(${name} PRIVATE ${ENTJOY_SIMD_DEFS} SIMD_MATH_PRECISION=${precision} NDEBUG NOMINMAX)
    target_compile_options(${name} PRIVATE ${ENTJOY_BASE_FLAGS} ${ENTJOY_SIMD_FLAGS})
    target_include_directories(${name} PRIVATE "${ENTJOY_NATIVE_DLL_DIR}")
endfunction()

entjoy_math_target(MathUlpCheck      ulp.cpp   1)
entjoy_math_target(MathUlpCheckHigh  ulp.cpp   2)

# 吞吐对照只在 AVX2 上编译：bench.cpp 直接用了 AVX2 intrinsics 与 NEON/AVX512 无关
if(ENTJOY_MATH_ISA STREQUAL "avx2")
    entjoy_math_target(MathExpBench      bench.cpp 1)
    entjoy_math_target(MathHighBench     bench.cpp 2)
endif()
