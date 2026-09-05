/*
 * mm_format — .NET's double-to-text, reproduced.
 *
 * Every number on the wire is a C# double written by System.Text.Json, which means
 * double.ToString() with no format: the SHORTEST digit string that parses back to the same
 * double, then one of two layouts —
 *
 *   plain       when the position of the decimal point, counted from the first significant
 *               digit (digPos = decimal exponent + 1), is within  -3 <= digPos <= max(digits, 17)
 *   scientific  otherwise: "d.dddE+XX" / "d.dddE-XX", uppercase E, explicit sign, at least two
 *               exponent digits
 *
 * so 1e16 is "10000000000000000", 1e17 is "1E+17", 123456789012345678 is "1.2345678901234568E+17",
 * 0.0001 is "0.0001", 0.00001 is "1E-05", 5e-324 is "5E-324", and negative zero is "-0".
 * (That is .NET's FormatGeneral with nMaxDigits = max(DigitsCount, 17) — the round-trip digit
 * count, not the 15 of the pre-Core-3.0 "G" formatter.) NaN and the infinities have no
 * canonical form (the C# writer throws) and are refused.
 *
 * Fixture: tests/Micromound.Tests/Golden/files/canonical-doubles.txt — ~300 bit patterns and the
 * text .NET wrote for each.
 */
#ifndef MM_FORMAT_H
#define MM_FORMAT_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Longest output: "-1.2345678901234567E+308" is 24; "-0.00012345678901234567" is 23. */
#define MM_FORMAT_DOUBLE_MAX 32

/*
 * Writes the canonical text of value into out (NUL-terminated) and returns its length, or 0 if
 * value is not finite or cap is too small.
 */
size_t mm_format_double(double value, char *out, size_t cap);

/* Writes a C# long / int the way JSON does: minus sign and decimal digits, nothing else. */
size_t mm_format_int(long long value, char *out, size_t cap);

#ifdef __cplusplus
}
#endif

#endif
