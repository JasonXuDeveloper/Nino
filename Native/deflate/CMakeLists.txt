cmake_minimum_required(VERSION 2.80)
project(Deflate)

set(CMAKE_CXX_STANDARD 14)
set(LIBRARY_OUTPUT_PATH ../../../src/Nino.Shared)

add_subdirectory(zlib)
add_library(Deflate SHARED library.cpp)
target_link_libraries(Deflate zlib)
include_directories(zlib)
#set(BUILD_RPATH build) # uncomment and change to your build path on some platform (i.e. windows to export vs project)
include_directories(${BUILD_RPATH}/zlib)