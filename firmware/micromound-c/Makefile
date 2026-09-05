# micromound-c — the portable C mirror of the MICROMOUND wire format (M5 groundwork).
#
#   make            builds build/libmicromound.a
#   make test       builds and runs the host tests against the golden fixtures in
#                   ../../tests/Micromound.Tests/Golden/files (override: GOLDEN_DIR=…)
#   make clean
#
#   make CC=clang test                                  another compiler
#   make OPT="-O0 -g" EXTRA_CFLAGS="-fsanitize=address,undefined" EXTRA_LDFLAGS="-fsanitize=address,undefined" test
#   make EXTRA_CFLAGS=-m32 EXTRA_LDFLAGS=-m32 test      a 32-bit build, when the toolchain has one
#
# Strict by default: C99, every warning an error. The library has no dependencies beyond the C
# standard library (snprintf/strtod in mm_format, memcpy, abort); the vendored TweetNaCl is
# compiled as part of mm_ed25519.c.

CC      ?= cc
AR      ?= ar
OPT     ?= -O2
EXTRA_CFLAGS  ?=
EXTRA_LDFLAGS ?=
CFLAGS  := $(OPT) -std=c99 -Wall -Wextra -Werror -pedantic -Iinclude -Ithird_party/tweetnacl $(EXTRA_CFLAGS)
LDFLAGS := $(EXTRA_LDFLAGS)
LDLIBS  := -lm

GOLDEN_DIR ?= ../../tests/Micromound.Tests/Golden/files

BUILD := build
LIB   := $(BUILD)/libmicromound.a
TESTS := $(BUILD)/mm_tests

LIB_SRC  := $(wildcard src/*.c)
LIB_OBJ  := $(patsubst src/%.c,$(BUILD)/%.o,$(LIB_SRC))
TEST_SRC := $(wildcard tests/*.c)
TEST_OBJ := $(patsubst tests/%.c,$(BUILD)/tests/%.o,$(TEST_SRC))

.PHONY: all test clean

all: $(LIB)

$(LIB): $(LIB_OBJ)
	$(AR) rcs $@ $^

$(BUILD)/%.o: src/%.c include/*.h | $(BUILD)
	$(CC) $(CFLAGS) -c $< -o $@

# mm_ed25519.c includes the vendored TweetNaCl source, so it depends on it too.
$(BUILD)/mm_ed25519.o: third_party/tweetnacl/tweetnacl.c third_party/tweetnacl/tweetnacl.h

$(BUILD)/tests/%.o: tests/%.c tests/mm_test.h include/*.h | $(BUILD)/tests
	$(CC) $(CFLAGS) -Itests -c $< -o $@

$(TESTS): $(TEST_OBJ) $(LIB)
	$(CC) $(LDFLAGS) $^ $(LDLIBS) -o $@

test: $(TESTS)
	./$(TESTS) $(GOLDEN_DIR)

$(BUILD) $(BUILD)/tests:
	mkdir -p $@

clean:
	rm -rf $(BUILD)
