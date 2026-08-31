cmake_minimum_required(VERSION 3.20)
project(JobSystemStage10ImplicitBatchToggle LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(NATIVE_DLL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll")
add_executable(Stage10_ImplicitBatchToggle Stage10_ImplicitBatchToggle.cpp
  ${NATIVE_DLL_DIR}/JobSystem.cpp ${NATIVE_DLL_DIR}/JobSystem_State.cpp
  ${NATIVE_DLL_DIR}/JobSystem_Tiles.cpp ${NATIVE_DLL_DIR}/JobSystem_Scheduler.cpp
  ${NATIVE_DLL_DIR}/JobProfiler.cpp ${NATIVE_DLL_DIR}/JobDebuggerGUI.cpp
  ${NATIVE_DLL_DIR}/ChaseLevScheduler.cpp)
target_include_directories(Stage10_ImplicitBatchToggle PRIVATE ${NATIVE_DLL_DIR})
if(MSVC)
  target_compile_options(Stage10_ImplicitBatchToggle PRIVATE /utf-8 /O2 /MP)
  target_compile_definitions(Stage10_ImplicitBatchToggle PRIVATE NDEBUG NOMINMAX)
else()
  target_compile_options(Stage10_ImplicitBatchToggle PRIVATE -O3 -pthread)
endif()
