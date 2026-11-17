cmake_minimum_required(VERSION 3.20)
project(MathUlpCheck CXX)

# 量 Fastest 档（SIMD_MATH_PRECISION=1）内联多项式的误差。
# 参数与 EntJoy 原生产物一致：AVX2、8 宽、无 fast-math。
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT DEFINED ENTJOY_NATIVE_DLL_DIR)
    set(ENTJOY_NATIVE_DLL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../src/NativeDll")
endif()

add_executable(MathUlpCheck ulp.cpp)
target_compile_definitions(MathUlpCheck PRIVATE NSIMD_AVX2 NSIMD_WIDTH=8 SIMD_MATH_PRECISION=1 NDEBUG NOMINMAX)
target_compile_options(MathUlpCheck PRIVATE /arch:AVX2 /O2 /fp:precise)
target_include_directories(MathUlpCheck PRIVATE "${ENTJOY_NATIVE_DLL_DIR}")

add_executable(MathExpBench bench.cpp)
target_compile_definitions(MathExpBench PRIVATE NSIMD_AVX2 NSIMD_WIDTH=8 SIMD_MATH_PRECISION=1 NDEBUG NOMINMAX)
target_compile_options(MathExpBench PRIVATE /arch:AVX2 /O2 /fp:precise)
target_include_directories(MathExpBench PRIVATE "${ENTJOY_NATIVE_DLL_DIR}")

add_executable(MathUlpCheckHigh ulp.cpp)
target_compile_definitions(MathUlpCheckHigh PRIVATE NSIMD_AVX2 NSIMD_WIDTH=8 SIMD_MATH_PRECISION=2 NDEBUG NOMINMAX)
target_compile_options(MathUlpCheckHigh PRIVATE /arch:AVX2 /O2 /fp:precise)
target_include_directories(MathUlpCheckHigh PRIVATE "${ENTJOY_NATIVE_DLL_DIR}")
add_executable(MathHighBench bench.cpp)
target_compile_definitions(MathHighBench PRIVATE NSIMD_AVX2 NSIMD_WIDTH=8 SIMD_MATH_PRECISION=2 NDEBUG NOMINMAX)
target_compile_options(MathHighBench PRIVATE /arch:AVX2 /O2 /fp:precise)
target_include_directories(MathHighBench PRIVATE "${ENTJOY_NATIVE_DLL_DIR}")