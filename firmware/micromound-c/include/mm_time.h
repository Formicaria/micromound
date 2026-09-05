/*
 * mm_time — protocol timestamps (PROTOCOL.md §2): `yyyy-MM-ddTHH:mm:ssZ`, UTC, second precision.
 *
 * Emitters produce exactly that form. Readers accept it and, as §2 asks, an offset form
 * (`…±HH:MM`) and fractional seconds (ignored), because a mound built against an older library is
 * better read than bricked. Nothing else parses. Times are int64 seconds since 1970-01-01T00:00:00Z
 * (proleptic Gregorian, no leap seconds — the same arithmetic DateTimeOffset uses).
 */
#ifndef MM_TIME_H
#define MM_TIME_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MM_TIME_TEXT_LEN 20   /* strlen("2026-08-14T21:04:11Z") */
#define MM_TIME_TEXT_CAP 40   /* room for the accepted offset/fractional forms, plus the NUL */

/* 0 on success with *epoch set; -1 for anything that is not a protocol timestamp. */
int mm_time_parse(const char *text, int64_t *epoch);

/* Writes the canonical form (NUL-terminated; out holds MM_TIME_TEXT_LEN + 1). Returns 20, or 0 if out of range. */
size_t mm_time_format(int64_t epoch, char *out, size_t cap);

#ifdef __cplusplus
}
#endif

#endif
