cmake_minimum_required(VERSION 3.20)
project(NativeDllCore LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(NATIVE_DLL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../src/NativeDll")

# 精简 NativeDll：只编译 C# P/Invoke 入口层 + 调度器核心，不含 imgui 调试面板
# （ENTJOY_IMGUI_ENABLED 不定义 → JobDebuggerGUI 为空实现）。供 C# Native 路径的
# CI 测试加载，无需 d3d11/OpenMP/AVX512 等 NativeDll.vcxproj 的调试依赖。
add_library(NativeDll SHARED
  ${NATIVE_DLL_DIR}/Exports.cpp
  ${NATIVE_DLL_DIR}/JobSystem.cpp
  ${NATIVE_DLL_DIR}/JobSystem_State.cpp
  ${NATIVE_DLL_DIR}/JobSystem_Tiles.cpp
  ${NATIVE_DLL_DIR}/JobSystem_Scheduler.cpp
  ${NATIVE_DLL_DIR}/ChaseLevScheduler.cpp
  ${NATIVE_DLL_DIR}/JobProfiler.cpp
  ${NATIVE_DLL_DIR}/JobDebuggerGUI.cpp
)
target_include_directories(NativeDll PRIVATE ${NATIVE_DLL_DIR})
target_compile_definitions(NativeDll PRIVATE JOB_SYSTEM_EXPORT NOMINMAX NDEBUG)

if(MSVC)
  target_compile_options(NativeDll PRIVATE /utf-8 /O2 /MP)
endif()
