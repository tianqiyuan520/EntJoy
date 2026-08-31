cmake_minimum_required(VERSION 3.20)
project(JobSystemAbiFixtures LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(stub_no_abi SHARED stub_no_abi.cpp)
add_library(stub_wrong_version SHARED stub_wrong_version.cpp)

if(MSVC)
  target_compile_options(stub_no_abi PRIVATE /utf-8)
  target_compile_options(stub_wrong_version PRIVATE /utf-8)
endif()
