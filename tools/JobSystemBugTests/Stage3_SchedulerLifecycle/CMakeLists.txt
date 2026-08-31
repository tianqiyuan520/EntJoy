cmake_minimum_required(VERSION 3.20)
project(JobSystemStage3SchedulerLifecycle LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(NATIVE_DLL_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../../src/NativeDll")
add_executable(Stage3_SchedulerLifecycle Stage3_SchedulerLifecycle.cpp
  ${NATIVE_DLL_DIR}/JobSystem.cpp ${NATIVE_DLL_DIR}/JobSystem_State.cpp
  ${NATIVE_DLL_DIR}/JobSystem_Tiles.cpp ${NATIVE_DLL_DIR}/JobSystem_Scheduler.cpp
  ${NATIVE_DLL_DIR}/JobProfiler.cpp ${NATIVE_DLL_DIR}/JobDebuggerGUI.cpp
  ${NATIVE_DLL_DIR}/ChaseLevScheduler.cpp)
target_include_directories(Stage3_SchedulerLifecycle PRIVATE ${NATIVE_DLL_DIR})
if(MSVC)
  target_compile_options(Stage3_SchedulerLifecycle PRIVATE /utf-8 /O2 /MP)
  target_compile_definitions(Stage3_SchedulerLifecycle PRIVATE NDEBUG NOMINMAX)
else()
  target_compile_options(Stage3_SchedulerLifecycle PRIVATE -O3 -pthread)
endif()
