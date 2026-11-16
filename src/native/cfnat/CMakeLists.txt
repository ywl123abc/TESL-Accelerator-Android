cmake_minimum_required(VERSION 3.20)
project(cfnat_core C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

include_directories(include src)

set(CFNAT_SOURCES
    src/cfnat_tls.c
    src/cfnat_api.c
    src/cfnat_jni.c
    src/cfnat_registry.c
)

# 动态库目标 (libcfnat.so / cfnat.dll)
add_library(cfnat SHARED ${CFNAT_SOURCES})
target_compile_definitions(cfnat PRIVATE CFNAT_BUILD_DLL)

if (ANDROID)
    target_link_libraries(cfnat PRIVATE log)
    target_link_options(cfnat PRIVATE "-Wl,-z,max-page-size=16384" "-Wl,-z,common-page-size=16384")
elseif (WIN32)
    target_link_libraries(cfnat PRIVATE ws2_32)
else()
    target_link_libraries(cfnat PRIVATE pthread)
endif()

# 独立 PoC 测试程序 (仅在非 Android 宿主下构建)
if (NOT ANDROID)
    add_executable(test_dual_instance
        ../../../tests/native_poc/test_dual_instance.c
        src/cfnat_tls.c
        src/cfnat_api.c
    )
    if (WIN32)
        target_link_libraries(test_dual_instance PRIVATE ws2_32)
    else()
        target_link_libraries(test_dual_instance PRIVATE pthread)
    endif()
endif()
