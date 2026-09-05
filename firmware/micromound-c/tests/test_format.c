#include "mm_test.h"
#include "mm_format.h"

#include <math.h>
#include <string.h>

static const char *fmt(double v, char *out)
{
    return mm_format_double(v, out, MM_FORMAT_DOUBLE_MAX) ? out : NULL;
}

void test_format(void)
{
    char out[MM_FORMAT_DOUBLE_MAX];

    /* The layout rule at its boundaries (the fixture file covers the bulk). */
    CHECK_STR_EQ("0", fmt(0.0, out));
    CHECK_STR_EQ("-0", fmt(-0.0, out));
    CHECK_STR_EQ("1", fmt(1.0, out));
    CHECK_STR_EQ("-1", fmt(-1.0, out));
    CHECK_STR_EQ("30", fmt(30.0, out));
    CHECK_STR_EQ("0.1", fmt(0.1, out));
    CHECK_STR_EQ("0.30000000000000004", fmt(0.1 + 0.2, out));
    CHECK_STR_EQ("0.3333333333333333", fmt(1.0 / 3, out));
    CHECK_STR_EQ("100000000000000", fmt(1e14, out));
    CHECK_STR_EQ("1000000000000000", fmt(1e15, out));
    CHECK_STR_EQ("10000000000000000", fmt(1e16, out));          /* digPos 17: still plain */
    CHECK_STR_EQ("1E+17", fmt(1e17, out));                      /* digPos 18 > 17: scientific */
    CHECK_STR_EQ("1E+20", fmt(1e20, out));
    CHECK_STR_EQ("12345678901234568", fmt(12345678901234567.0, out)); /* 17 digits, digPos 17: plain */
    CHECK_STR_EQ("1.2345678901234568E+17", fmt(123456789012345678.0, out)); /* digPos 18: scientific */
    CHECK_STR_EQ("999999999999999.9", fmt(999999999999999.9, out));
    CHECK_STR_EQ("1E+21", fmt(1e21, out));
    CHECK_STR_EQ("1E+100", fmt(1e100, out));
    CHECK_STR_EQ("1.7976931348623157E+308", fmt(1.7976931348623157e308, out));
    CHECK_STR_EQ("0.001", fmt(0.001, out));
    CHECK_STR_EQ("0.0001", fmt(0.0001, out));                    /* digPos -3: plain */
    CHECK_STR_EQ("1E-05", fmt(0.00001, out));                    /* digPos -4: scientific, two exponent digits */
    CHECK_STR_EQ("1.23E-05", fmt(1.23e-5, out));
    CHECK_STR_EQ("1E-07", fmt(1e-7, out));
    CHECK_STR_EQ("5E-324", fmt(5e-324, out));                    /* the smallest subnormal */
    CHECK_STR_EQ("2.2250738585072014E-308", fmt(2.2250738585072014e-308, out));
    CHECK_STR_EQ("-273.15", fmt(-273.15, out));
    CHECK_STR_EQ("6.02214076E+23", fmt(6.02214076e23, out));
    CHECK_STR_EQ("1.602176634E-19", fmt(1.602176634e-19, out));
    CHECK_STR_EQ("4.096", fmt(4.096, out));
    CHECK_STR_EQ("123456789", fmt(123456789.0, out));
    CHECK_STR_EQ("1000000", fmt(1000000.0, out));

    /* Not finite: refused. */
    CHECK(mm_format_double(HUGE_VAL, out, sizeof out) == 0);
    CHECK(mm_format_double(-HUGE_VAL, out, sizeof out) == 0);
    CHECK(mm_format_double(HUGE_VAL - HUGE_VAL, out, sizeof out) == 0);

    /* Too small a buffer: refused, not truncated. */
    CHECK(mm_format_double(1.7976931348623157e308, out, 10) == 0);
    CHECK(mm_format_double(1.0, out, 1) == 0);
    CHECK(mm_format_double(1.0, out, 2) == 1);

    /* Integers. */
    CHECK(mm_format_int(0, out, sizeof out) == 1); CHECK_STR_EQ("0", out);
    CHECK(mm_format_int(-1, out, sizeof out) == 2); CHECK_STR_EQ("-1", out);
    CHECK(mm_format_int(900, out, sizeof out) == 3); CHECK_STR_EQ("900", out);
    CHECK(mm_format_int(9223372036854775807LL, out, sizeof out) == 19); CHECK_STR_EQ("9223372036854775807", out);
    CHECK(mm_format_int(-9223372036854775807LL - 1, out, sizeof out) == 20); CHECK_STR_EQ("-9223372036854775808", out);
    CHECK(mm_format_int(123, out, 3) == 0);
}
