cmake_minimum_required(VERSION 3.20)
project(EntJoyNativeComparison LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# 优化档位固定成显式变量：同一框架换 -O2 / -O3 会产生不同读数，必须写进结果行。
set(ENTJOY_OPT_NUMBER "2" CACHE STRING "Optimization level passed to -O (2 or 3)")

# Flecs 4.2.0 amalgamated 源。no_addons 变体只保留核心与查询，额外依赖 dbghelp。
add_library(flecs_core STATIC third_party/flecs/distr/flecs_no_addons.c)
target_include_directories(flecs_core PUBLIC third_party/flecs/distr)

add_executable(native-comparison main.cpp)
target_include_directories(native-comparison PRIVATE third_party/entt/src)
target_link_libraries(native-comparison PRIVATE flecs_core)

if(MSVC)
  target_compile_options(flecs_core PRIVATE /O2)
  target_compile_options(native-comparison PRIVATE /O2)
  target_compile_definitions(native-comparison PRIVATE ENTJOY_OPT_NUMBER=2)
else()
  # clang 与 gcc 走同一组参数，避免两臂参数不一致。
  target_compile_options(flecs_core PRIVATE -O${ENTJOY_OPT_NUMBER})
  target_compile_options(native-comparison PRIVATE -O${ENTJOY_OPT_NUMBER})
  target_compile_definitions(native-comparison PRIVATE ENTJOY_OPT_NUMBER=${ENTJOY_OPT_NUMBER})
endif()

# Flecs 核心的诊断栈回溯依赖 dbghelp，MSVC 与 MinGW 都需要。
if(WIN32)
  target_link_libraries(native-comparison PRIVATE dbghelp)
endif()
