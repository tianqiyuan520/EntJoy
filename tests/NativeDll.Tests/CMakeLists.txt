cmake_minimum_required(VERSION 3.20)
project(EntJoyJobSystemTests LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(JobSystemTests
    JobSystemTests.cpp
    ../../src/NativeDll/JobSystem.cpp
    ../../src/NativeDll/JobSystem_State.cpp
    ../../src/NativeDll/JobSystem_Tiles.cpp
    ../../src/NativeDll/JobSystem_Scheduler.cpp
    ../../src/NativeDll/JobProfiler.cpp
    ../../src/NativeDll/JobDebuggerGUI.cpp
    ../../src/NativeDll/ChaseLevScheduler.cpp
)

add_executable(AssistLifetimeTests
    AssistLifetimeTests.cpp
    ../../src/NativeDll/JobSystem.cpp
    ../../src/NativeDll/JobSystem_State.cpp
    ../../src/NativeDll/JobSystem_Tiles.cpp
    ../../src/NativeDll/JobSystem_Scheduler.cpp
    ../../src/NativeDll/JobProfiler.cpp
    ../../src/NativeDll/JobDebuggerGUI.cpp
    ../../src/NativeDll/ChaseLevScheduler.cpp
)

add_executable(ChaseLevIntegrationTests
    ChaseLevIntegrationTests.cpp
    ../../src/NativeDll/JobSystem.cpp
    ../../src/NativeDll/JobSystem_State.cpp
    ../../src/NativeDll/JobSystem_Tiles.cpp
    ../../src/NativeDll/JobSystem_Scheduler.cpp
    ../../src/NativeDll/JobProfiler.cpp
    ../../src/NativeDll/JobDebuggerGUI.cpp
    ../../src/NativeDll/ChaseLevScheduler.cpp
)

add_executable(SparseTileDequeTests
    SparseTileDequeTests.cpp
)

add_executable(SparseTileDequeSmoketest
    SparseTileDequeSmoketest.cpp
)

add_executable(SparseTileDequeTrace
    SparseTileDequeTrace.cpp
)

add_executable(MPMCInjectorTests
    MPMCInjectorTests.cpp
)

add_executable(ImplicitBatchTests
    ImplicitBatchTests.cpp
    ../../src/NativeDll/JobSystem.cpp
    ../../src/NativeDll/JobSystem_State.cpp
    ../../src/NativeDll/JobSystem_Tiles.cpp
    ../../src/NativeDll/JobSystem_Scheduler.cpp
    ../../src/NativeDll/JobProfiler.cpp
    ../../src/NativeDll/JobDebuggerGUI.cpp
    ../../src/NativeDll/ChaseLevScheduler.cpp
)

target_include_directories(JobSystemTests PRIVATE
    ../../src/NativeDll
)

target_include_directories(AssistLifetimeTests PRIVATE
    ../../src/NativeDll
)

target_include_directories(ChaseLevIntegrationTests PRIVATE
    ../../src/NativeDll
)

target_include_directories(SparseTileDequeTests PRIVATE
    ../../src/NativeDll
)

target_include_directories(SparseTileDequeSmoketest PRIVATE
    ../../src/NativeDll
)

target_include_directories(SparseTileDequeTrace PRIVATE
    ../../src/NativeDll
)

target_include_directories(MPMCInjectorTests PRIVATE
    ../../src/NativeDll
)

target_include_directories(ImplicitBatchTests PRIVATE
    ../../src/NativeDll
)

if(MSVC)
    # MSVC SDL 会把 CRT deprecated 警告（C4996，如 getenv）升级为 error；
    # _CRT_SECURE_NO_WARNINGS 统一抑制（Clang 不识别该宏，无副作用）。
    add_compile_definitions(_CRT_SECURE_NO_WARNINGS)
    # 源文件为 UTF-8 无 BOM（含中文注释）。不加 /utf-8 时 MSVC 按本地代码页
    # (936) 解析，多字节注释破坏行结构，报出大量与磁盘内容对不上的伪错误。
    target_compile_options(JobSystemTests PRIVATE /utf-8 /O2 /Ob2 /Oi /Ot /arch:AVX2 /MP)
    target_compile_definitions(JobSystemTests PRIVATE NDEBUG NOMINMAX ENTJOY_TESTING)

    target_compile_options(AssistLifetimeTests PRIVATE /utf-8 /O2 /Ob2 /Oi /Ot /arch:AVX2)
    target_compile_definitions(AssistLifetimeTests PRIVATE NOMINMAX)

    target_compile_options(ChaseLevIntegrationTests PRIVATE /utf-8 /O2 /Ob2 /Oi /Ot /arch:AVX2 /MP)
    target_compile_definitions(ChaseLevIntegrationTests PRIVATE NDEBUG NOMINMAX)

    target_compile_options(SparseTileDequeTests PRIVATE /utf-8 /O2 /Ob2 /Oi /Ot /arch:AVX2 /MP)
    target_compile_definitions(SparseTileDequeTests PRIVATE NDEBUG NOMINMAX)

    target_compile_options(SparseTileDequeSmoketest PRIVATE /utf-8 /O2 /Ob2 /Oi /Ot /arch:AVX2 /MP)
    target_compile_definitions(SparseTileDequeSmoketest PRIVATE NDEBUG NOMINMAX)

    target_compile_options(SparseTileDequeTrace PRIVATE /utf-8 /Od /arch:AVX2 /MP)
    target_compile_definitions(SparseTileDequeTrace PRIVATE NOMINMAX)

    target_compile_options(MPMCInjectorTests PRIVATE /utf-8 /O2 /Ob2 /Oi /Ot /arch:AVX2 /MP)
    target_compile_definitions(MPMCInjectorTests PRIVATE NDEBUG NOMINMAX)

    target_compile_options(ImplicitBatchTests PRIVATE /utf-8 /O2 /Ob2 /Oi /Ot /arch:AVX2 /MP)
    target_compile_definitions(ImplicitBatchTests PRIVATE NDEBUG NOMINMAX)
else()
    # Linux/Clang：std::thread 需要 pthread；sanitizer flags 由 CI 的 CMAKE_CXX_FLAGS 传入。
    add_compile_options(-pthread)
    add_link_options(-pthread)
endif()
