/* A test harness small enough to read in one sitting: counters, CHECK, and a per-file entry point. */
#ifndef MM_TEST_H
#define MM_TEST_H

#include <stdio.h>
#include <string.h>

extern int mm_test_checks;
extern int mm_test_failures;

#define CHECK(cond) do { \
    mm_test_checks++; \
    if (!(cond)) { mm_test_failures++; printf("  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); } \
} while (0)

#define CHECK_STR_EQ(expected, actual) do { \
    const char *e_ = (expected), *a_ = (actual); \
    mm_test_checks++; \
    if (e_ == NULL || a_ == NULL || strcmp(e_, a_) != 0) { \
        mm_test_failures++; \
        printf("  FAIL %s:%d:\n    expected: %s\n    actual:   %s\n", __FILE__, __LINE__, e_ ? e_ : "(null)", a_ ? a_ : "(null)"); \
    } \
} while (0)

#define CHECK_MEM_EQ(expected, actual, n) do { \
    mm_test_checks++; \
    if (memcmp((expected), (actual), (n)) != 0) { mm_test_failures++; printf("  FAIL %s:%d: %d bytes differ\n", __FILE__, __LINE__, (int)(n)); } \
} while (0)

/* The directory holding the golden .txt files, from the command line. */
extern const char *mm_test_golden_dir;

void test_json(void);
void test_format(void);
void test_sha256(void);
void test_ed25519(void);
void test_envelope(void);
void test_golden(void);

#endif
