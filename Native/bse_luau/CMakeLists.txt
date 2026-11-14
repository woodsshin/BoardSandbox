cmake_minimum_required(VERSION 3.16)
project(bse_luau LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

option(BSE_SANITIZE "Instrument the library and tests with AddressSanitizer and UBSan (GCC/Clang)" OFF)
if(BSE_SANITIZE)
  add_compile_options(-fsanitize=address,undefined -fno-omit-frame-pointer -fno-sanitize-recover=undefined)
  add_link_options(-fsanitize=address,undefined)
endif()

if(MSVC)
  set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  set(LUAU_STATIC_CRT ON CACHE BOOL "" FORCE)
endif()

set(BSE_LUAU_TAG "0.745" CACHE STRING "Luau release tag")
set(BSE_LUAU_COMMIT "2a3d8937c19c8fd9f7b72b6ec9068c62760e6588" CACHE STRING "Luau commit that BSE_LUAU_TAG resolves to")
set(LUAU_SOURCE_DIR "" CACHE PATH "Existing Luau checkout; fetched from GitHub when empty")

if(LUAU_SOURCE_DIR)
  set(bse_luau_src "${LUAU_SOURCE_DIR}")
else()
  if(POLICY CMP0169)
    cmake_policy(SET CMP0169 OLD)
  endif()
  include(FetchContent)
  FetchContent_Declare(luau GIT_REPOSITORY https://github.com/luau-lang/luau.git GIT_TAG ${BSE_LUAU_COMMIT})
  FetchContent_GetProperties(luau)
  if(NOT luau_POPULATED)
    FetchContent_Populate(luau)
  endif()
  set(bse_luau_src "${luau_SOURCE_DIR}")
endif()

set(LUAU_BUILD_CLI OFF CACHE BOOL "" FORCE)
set(LUAU_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(LUAU_BUILD_WEB OFF CACHE BOOL "" FORCE)
add_subdirectory("${bse_luau_src}" luau EXCLUDE_FROM_ALL)

set(BSE_PRELUDE "${CMAKE_CURRENT_SOURCE_DIR}/luau/prelude.luau")
set(BSE_PRELUDE_CPP "${CMAKE_CURRENT_BINARY_DIR}/prelude_data.cpp")
add_custom_command(
  OUTPUT "${BSE_PRELUDE_CPP}"
  COMMAND ${CMAKE_COMMAND} -DINPUT=${BSE_PRELUDE} -DOUTPUT=${BSE_PRELUDE_CPP} -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/embed.cmake"
  DEPENDS "${BSE_PRELUDE}" "${CMAKE_CURRENT_SOURCE_DIR}/cmake/embed.cmake"
  VERBATIM)

add_library(bse_luau SHARED
  src/bse_luau.cpp
  src/json.cpp
  "${BSE_PRELUDE_CPP}")
target_include_directories(bse_luau PRIVATE include src)
target_compile_definitions(bse_luau PRIVATE BSE_BUILDING=1 BSE_LUAU_VERSION="${BSE_LUAU_TAG}")
target_link_libraries(bse_luau PRIVATE Luau.VM Luau.Compiler)

if(MSVC)
  target_compile_options(bse_luau PRIVATE /W4)
else()
  target_compile_options(bse_luau PRIVATE -Wall -Wextra)
endif()

if(WIN32)
  set_target_properties(bse_luau PROPERTIES PREFIX "")
endif()

if(CMAKE_SYSTEM_NAME MATCHES "^(Linux|Android)$")
  set(bse_exports "${CMAKE_CURRENT_SOURCE_DIR}/cmake/exports.map")
  target_link_options(bse_luau PRIVATE "-Wl,--version-script=${bse_exports}" -Wl,-z,relro,-z,now)
  set_property(TARGET bse_luau APPEND PROPERTY LINK_DEPENDS "${bse_exports}")
  if(CMAKE_CXX_COMPILER_ID STREQUAL "GNU" AND NOT BSE_SANITIZE)
    target_link_options(bse_luau PRIVATE -static-libstdc++ -static-libgcc)
  endif()
endif()

if(ANDROID)
  # Devices with 16 KB memory pages (Android 15 and later) refuse libraries aligned for 4 KB ones.
  target_link_options(bse_luau PRIVATE -Wl,-z,max-page-size=16384)
endif()

enable_testing()
add_executable(bse_luau_tests tests/native_tests.c)
target_include_directories(bse_luau_tests PRIVATE include)
target_compile_definitions(bse_luau_tests PRIVATE BSE_TEST_DIR="${CMAKE_CURRENT_SOURCE_DIR}/tests/luau")
target_link_libraries(bse_luau_tests PRIVATE bse_luau)
if(NOT WIN32)
  find_package(Threads REQUIRED)
  target_link_libraries(bse_luau_tests PRIVATE Threads::Threads)
endif()
set_target_properties(bse_luau_tests PROPERTIES EXCLUDE_FROM_ALL ON)
add_test(NAME bse_luau_native COMMAND bse_luau_tests)

option(BSE_BUILD_TOOLS "Build developer tools (bse_analyze)" OFF)
if(BSE_BUILD_TOOLS)
  add_executable(bse_analyze tools/bse_analyze/bse_analyze.cpp)
  target_link_libraries(bse_analyze PRIVATE Luau.Analysis Luau.Config Luau.CLI.lib)
  find_package(Threads REQUIRED)
  target_link_libraries(bse_analyze PRIVATE Threads::Threads)
endif()
