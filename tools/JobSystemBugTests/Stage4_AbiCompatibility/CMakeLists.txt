cmake_minimum_required(VERSION 3.20)
project(JobSystemAbiFixture LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(NATIVE_DLL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll")
add_library(NativeDll SHARED
  ${NATIVE_DLL_DIR}/Exports.cpp ${NATIVE_DLL_DIR}/JobSystem.cpp
  ${NATIVE_DLL_DIR}/JobSystem_State.cpp ${NATIVE_DLL_DIR}/JobSystem_Tiles.cpp
  ${NATIVE_DLL_DIR}/JobSystem_Scheduler.cpp ${NATIVE_DLL_DIR}/JobProfiler.cpp
  ${NATIVE_DLL_DIR}/JobDebuggerGUI.cpp ${NATIVE_DLL_DIR}/ChaseLevScheduler.cpp)
target_include_directories(NativeDll PRIVATE ${NATIVE_DLL_DIR})
target_compile_definitions(NativeDll PRIVATE JOB_SYSTEM_EXPORT NOMINMAX)
set_target_properties(NativeDll PROPERTIES OUTPUT_NAME "NativeDll")
if(MSVC)
  target_compile_options(NativeDll PRIVATE /utf-8 /O2 /MP)
else()
  target_compile_options(NativeDll PRIVATE -O3 -pthread)
endif()
