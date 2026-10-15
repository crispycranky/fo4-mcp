cmake_minimum_required(VERSION 3.18)
project(F4Exec LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

set(EXTERN ${CMAKE_CURRENT_SOURCE_DIR}/../extern)

add_library(F4Exec SHARED F4Exec.cpp)
target_include_directories(F4Exec PRIVATE ${EXTERN}/f4se ${EXTERN}/common)
target_compile_options(F4Exec PRIVATE /FI"common/IPrefix.h" /EHa /W3)
target_compile_definitions(F4Exec PRIVATE _CRT_SECURE_NO_WARNINGS)
