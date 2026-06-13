cmake_minimum_required(VERSION 3.10)
project(NativeDll LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include_directories(${CMAKE_CURRENT_SOURCE_DIR})
include_directories("D:/Godot/Project/EntJoy/src/NativeDll")

# No explicit task system defined; tasksys.cpp will pick the best one for the platform
if(EXISTS "D:/Godot/Project/EntJoy/src/NativeDll/tasksys.cpp")
    set(TASKSYS_SRC "D:/Godot/Project/EntJoy/src/NativeDll/tasksys.cpp")
else()
    set(TASKSYS_SRC "")
    message(WARNING "tasksys.cpp not found at D:/Godot/Project/EntJoy/src/NativeDll/tasksys.cpp")
endif()

add_library(NativeDll SHARED
    "D:/Godot/Project/EntJoy/src/NativeDll/Exports.cpp"
    "D:/Godot/Project/EntJoy/src/NativeDll/JobProfiler.cpp"
    "D:/Godot/Project/EntJoy/src/NativeDll/JobSystem.cpp"
    "D:/Godot/Project/EntJoy/src/NativeDll/Native.cpp"
    SharpNative_Job__global_namespace__MoveSystemJobCpp_Execute.cpp
    SharpNative_Job__global_namespace__MoveSystemJobCpp_Execute_Adapter.cpp
    SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute_wrapper.cpp
    SharpNative_Job__global_namespace__NativeMoveJob_NativeCpp_Execute.cpp
    SharpNative_Job__global_namespace__NativeMoveJob_NativeCpp_Execute_Adapter.cpp
    SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch_wrapper.cpp
    SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Adapter.cpp
    ${TASKSYS_SRC}
)

if(TASKSYS_SRC)
    set_source_files_properties(${TASKSYS_SRC} PROPERTIES COMPILE_FLAGS "/arch:AVX")
endif()

find_program(ISPC_EXECUTABLE ispc)
if(NOT ISPC_EXECUTABLE)
    set(ISPC_EXECUTABLE "E:/Code/ispc-v1.30.0-windows/bin/ispc.exe")
endif()

add_custom_command(TARGET NativeDll PRE_BUILD
    COMMAND "${CMAKE_CURRENT_SOURCE_DIR}/run_ispc.bat"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
    COMMENT "Running ISPC compiler for generated .ispc files"
)

set(ISPC_OBJECTS
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job__global_namespace__MoveSystemJobIspc_Execute.obj"
    "${CMAKE_CURRENT_BINARY_DIR}/SharpNative_Job__global_namespace__NativeMoveJob_NativeIspc_Execute_Batch.obj"
)
target_link_libraries(NativeDll PRIVATE ${ISPC_OBJECTS})

if(MSVC)
    target_compile_options(NativeDll PRIVATE /std:c++20 /O2 /Ob2 /Oi /Ot /GL /arch:AVX2 /Qpar)
    target_compile_definitions(NativeDll PRIVATE NDEBUG NOMINMAX NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)
    set_target_properties(NativeDll PROPERTIES INTERPROCEDURAL_OPTIMIZATION TRUE)
else()
    target_compile_options(NativeDll PRIVATE -O3 -march=native -mtune=native -ffast-math -ffp-contract=fast -fno-signed-zeros -fno-trapping-math -funroll-loops -fstrict-aliasing -fomit-frame-pointer)
    target_compile_definitions(NativeDll PRIVATE NDEBUG NATIVEDLL_EXPORTS JOB_SYSTEM_EXPORT)
endif()

set_target_properties(NativeDll PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "D:/Godot/Project/EntJoy/bin"
    LIBRARY_OUTPUT_DIRECTORY "D:/Godot/Project/EntJoy/bin"
    ARCHIVE_OUTPUT_DIRECTORY "D:/Godot/Project/EntJoy/bin"
)

foreach(OUTPUTCONFIG ${CMAKE_CONFIGURATION_TYPES})
    string(TOUPPER ${OUTPUTCONFIG} OUTPUTCONFIG)
    set_target_properties(NativeDll PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY_${OUTPUTCONFIG} "D:/Godot/Project/EntJoy/bin"
        LIBRARY_OUTPUT_DIRECTORY_${OUTPUTCONFIG} "D:/Godot/Project/EntJoy/bin"
        ARCHIVE_OUTPUT_DIRECTORY_${OUTPUTCONFIG} "D:/Godot/Project/EntJoy/bin"
    )
endforeach()

if(WIN32)
    set_target_properties(NativeDll PROPERTIES SUFFIX ".dll")
endif()
