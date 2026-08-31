cmake_minimum_required(VERSION 3.20)
project(ISPCWidthBoundaryTest LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 17)

find_program(ISPC_EXECUTABLE ispc REQUIRED)

# 三个 SIMD 宽度目标：sse4(4-wide) / avx2(8-wide) / avx512skx-x16(16-wide)
set(ISPC_TARGETS sse4 avx2 avx512)
set(ISPC_FLAGS   sse4 avx2 avx512skx-x16)

foreach(i RANGE 0 2)
    list(GET ISPC_TARGETS ${i} tgt)
    list(GET ISPC_FLAGS ${i} flag)
    set(obj ${CMAKE_CURRENT_BINARY_DIR}/WidthBoundaryTest_${tgt}.obj)
    add_custom_command(
        OUTPUT ${obj}
        COMMAND ${ISPC_EXECUTABLE} ${CMAKE_CURRENT_SOURCE_DIR}/WidthBoundaryTest.ispc
                -o ${obj} --target=${flag} --emit-obj --arch=x86-64
        DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/WidthBoundaryTest.ispc
        COMMENT "Compiling ISPC (${flag})"
    )
    add_executable(width_test_${tgt} main.cpp ${obj})
    if(MSVC)
        target_compile_options(width_test_${tgt} PRIVATE /O2 /utf-8)
    endif()
endforeach()
