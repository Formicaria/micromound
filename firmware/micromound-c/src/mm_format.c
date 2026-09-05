#include "mm_format.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/*
 * Shortest round-trip digits via the C library: print with p significant digits for p = 1..17
 * and take the first p whose text parses back to the same double. With a correctly rounded
 * printf and strtod (glibc, musl, newlib, MSVCRT since 2015) the p-digit text is the p-digit
 * decimal nearest the value, so the first p that round-trips is exactly the shortest
 * round-trippable string — the same digits .NET's Ryu/Grisu-style formatter picks. The fixture
 * canonical-doubles.txt is the check that this holds for the libc in use, including the
 * subnormals and the E+308 edge.
 *
 * Fills digits (no sign, no dot, no trailing zeros; at least one digit) and *exp10 such that
 * |value| = d.ddd × 10^exp10. Returns the digit count.
 */
static int shortest_digits(double value, char digits[18], int *exp10)
{
    char text[40];
    int p;
    double a = fabs(value);

    for (p = 1; p <= 17; p++) {
        char *e;
        int n = 0;
        const char *c;

        snprintf(text, sizeof text, "%.*e", p - 1, a);
        if (strtod(text, NULL) != a && p < 17) continue;

        /* d[.ddd]e[+-]XX → digits and exponent */
        for (c = text; *c != 'e' && *c != 'E' && *c != '\0'; c++)
            if (*c >= '0' && *c <= '9' && n < 17) digits[n++] = *c;
        while (n > 1 && digits[n - 1] == '0') n--;
        digits[n] = '\0';
        e = strchr(text, 'e');
        *exp10 = e ? (int)strtol(e + 1, NULL, 10) : 0;
        return n;
    }
    return 0; /* unreachable: 17 digits always round-trip */
}

size_t mm_format_double(double value, char *out, size_t cap)
{
    char digits[18];
    char buf[MM_FORMAT_DOUBLE_MAX];
    size_t n = 0;
    int count, exp10, dig_pos, i;

    if (!isfinite(value) || cap == 0) return 0;

    if (value == 0.0) {
        /* .NET writes negative zero as "-0". */
        const char *z = signbit(value) ? "-0" : "0";
        size_t zl = strlen(z);
        if (zl + 1 > cap) return 0;
        memcpy(out, z, zl + 1);
        return zl;
    }

    count = shortest_digits(value, digits, &exp10);
    dig_pos = exp10 + 1;                                  /* .NET: number.Scale */

    if (value < 0) buf[n++] = '-';

    if (dig_pos > (count > 17 ? count : 17) || dig_pos < -3) {
        /* scientific: d.dddE+XX */
        int e = dig_pos - 1, ae = e < 0 ? -e : e;
        buf[n++] = digits[0];
        if (count > 1) {
            buf[n++] = '.';
            for (i = 1; i < count; i++) buf[n++] = digits[i];
        }
        buf[n++] = 'E';
        buf[n++] = e < 0 ? '-' : '+';
        if (ae >= 100) buf[n++] = (char)('0' + ae / 100);
        buf[n++] = (char)('0' + (ae / 10) % 10);
        buf[n++] = (char)('0' + ae % 10);
    } else if (dig_pos <= 0) {
        /* 0.000ddd */
        buf[n++] = '0';
        buf[n++] = '.';
        for (i = 0; i < -dig_pos; i++) buf[n++] = '0';
        for (i = 0; i < count; i++) buf[n++] = digits[i];
    } else if (dig_pos < count) {
        /* ddd.ddd */
        for (i = 0; i < dig_pos; i++) buf[n++] = digits[i];
        buf[n++] = '.';
        for (i = dig_pos; i < count; i++) buf[n++] = digits[i];
    } else {
        /* ddd000 */
        for (i = 0; i < count; i++) buf[n++] = digits[i];
        for (i = count; i < dig_pos; i++) buf[n++] = '0';
    }

    if (n + 1 > cap) return 0;
    memcpy(out, buf, n);
    out[n] = '\0';
    return n;
}

size_t mm_format_int(long long value, char *out, size_t cap)
{
    char tmp[24];
    size_t n = 0, i;
    unsigned long long u = value < 0 ? 0ULL - (unsigned long long)value : (unsigned long long)value;

    do { tmp[n++] = (char)('0' + u % 10); u /= 10; } while (u);
    if (value < 0) tmp[n++] = '-';
    if (n + 1 > cap) return 0;
    for (i = 0; i < n; i++) out[i] = tmp[n - 1 - i];
    out[n] = '\0';
    return n;
}
