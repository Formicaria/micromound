# firmware/micromound-c as an ESP-IDF component: the same sources the host tests build, unchanged.
set(MM_ROOT "${CMAKE_CURRENT_LIST_DIR}/../../../micromound-c")
file(GLOB MM_SRCS "${MM_ROOT}/src/*.c")

idf_component_register(
    SRCS ${MM_SRCS}
    INCLUDE_DIRS "${MM_ROOT}/include"
    PRIV_INCLUDE_DIRS "${MM_ROOT}/third_party/tweetnacl")

# The library is C99 and warning-clean under -Wall -Wextra -Werror -pedantic on the host; keep it so here.
target_compile_options(${COMPONENT_LIB} PRIVATE -std=c99 -Wall -Wextra)
