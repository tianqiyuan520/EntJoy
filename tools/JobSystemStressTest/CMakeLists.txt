cmake_minimum_required(VERSION 3.20)
project(JobSystemStressTest LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ── 源文件 ──
set(NATIVE_DLL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../src/NativeDll")

add_executable(JobSystemStressTest
    src/StressTest.cpp
    ${NATIVE_DLL_DIR}/JobSystem.cpp
    ${NATIVE_DLL_DIR}/JobSystem_State.cpp
    ${NATIVE_DLL_DIR}/JobSystem_Tiles.cpp
    ${NATIVE_DLL_DIR}/JobSystem_Scheduler.cpp
    ${NATIVE_DLL_DIR}/JobProfiler.cpp
    ${NATIVE_DLL_DIR}/JobDebuggerGUI.cpp
    ${NATIVE_DLL_DIR}/ChaseLevScheduler.cpp
)

target_include_directories(JobSystemStressTest PRIVATE
    ${NATIVE_DLL_DIR}
)

if(MSVC)
    target_compile_options(JobSystemStressTest PRIVATE /utf-8 /O2 /Ob2 /Oi /Ot /arch:AVX2 /MP)
    target_compile_definitions(JobSystemStressTest PRIVATE NDEBUG NOMINMAX)
else()
    target_compile_options(JobSystemStressTest PRIVATE -O3 -march=native -pthread)
    target_compile_definitions(JobSystemStressTest PRIVATE NDEBUG NOMINMAX)
endif()
