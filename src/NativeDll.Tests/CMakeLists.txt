cmake_minimum_required(VERSION 3.20)
project(EntJoyJobSystemTests LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(JobSystemTests
    JobSystemTests.cpp
    ../NativeDll/JobSystem.cpp
    ../NativeDll/JobSystem_State.cpp
    ../NativeDll/JobSystem_Tiles.cpp
    ../NativeDll/JobSystem_Scheduler.cpp
    ../NativeDll/JobProfiler.cpp
    ../NativeDll/JobDebuggerGUI.cpp
    ../NativeDll/NativeWorkerPool.cpp
)

add_executable(AssistLifetimeTests
    AssistLifetimeTests.cpp
    ../NativeDll/JobSystem.cpp
    ../NativeDll/JobSystem_State.cpp
    ../NativeDll/JobSystem_Tiles.cpp
    ../NativeDll/JobSystem_Scheduler.cpp
    ../NativeDll/JobProfiler.cpp
    ../NativeDll/JobDebuggerGUI.cpp
    ../NativeDll/NativeWorkerPool.cpp
)

add_executable(NativeWorkerPoolTests
    NativeWorkerPoolTests.cpp
    ../NativeDll/NativeWorkerPool.cpp
)

target_include_directories(JobSystemTests PRIVATE
    ../NativeDll
)

target_include_directories(AssistLifetimeTests PRIVATE
    ../NativeDll
)

target_include_directories(NativeWorkerPoolTests PRIVATE
    ../NativeDll
)

if(MSVC)
    # 源文件为 UTF-8 无 BOM（含中文注释）。不加 /utf-8 时 MSVC 按本地代码页
    # (936) 解析，多字节注释破坏行结构，报出大量与磁盘内容对不上的伪错误。
    target_compile_options(JobSystemTests PRIVATE /utf-8 /O2 /Ob2 /Oi /Ot /arch:AVX2 /MP)
    target_compile_definitions(JobSystemTests PRIVATE NDEBUG NOMINMAX)

    target_compile_options(AssistLifetimeTests PRIVATE /utf-8 /O2 /Ob2 /Oi /Ot /arch:AVX2)
    target_compile_definitions(AssistLifetimeTests PRIVATE NOMINMAX)

    target_compile_options(NativeWorkerPoolTests PRIVATE /utf-8 /O2 /Ob2 /Oi /Ot /MP)
    target_compile_definitions(NativeWorkerPoolTests PRIVATE NDEBUG NOMINMAX)
endif()
