cmake_minimum_required(VERSION 3.20)
project(EntJoyJobSystemTests LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(JobSystemTests
    JobSystemTests.cpp
    ../NativeDll/JobSystem.cpp
    ../NativeDll/JobProfiler.cpp
)

add_executable(AssistLifetimeTests
    AssistLifetimeTests.cpp
    ../NativeDll/JobSystem.cpp
    ../NativeDll/JobProfiler.cpp
)

target_include_directories(JobSystemTests PRIVATE
    ../NativeDll
    ../../external/cpp-taskflow
)

target_include_directories(AssistLifetimeTests PRIVATE
    ../NativeDll
    ../../external/cpp-taskflow
)

if(MSVC)
    target_compile_options(JobSystemTests PRIVATE /O2 /Ob2 /Oi /Ot /arch:AVX2 /MP)
    target_compile_definitions(JobSystemTests PRIVATE NDEBUG NOMINMAX)

    target_compile_options(AssistLifetimeTests PRIVATE /O2 /Ob2 /Oi /Ot /arch:AVX2)
    target_compile_definitions(AssistLifetimeTests PRIVATE NOMINMAX)
endif()
