#include "mm_test.h"
#include "mm_time.h"

#include <string.h>

static int64_t parse(const char *s)
{
    int64_t t = -12345;
    return mm_time_parse(s, &t) == 0 ? t : -1;
}

void test_time(void)
{
    char out[MM_TIME_TEXT_CAP];
    int64_t t;

    /* Anchors: the epoch, the golden fixture's instant, the far edges DateTimeOffset reaches. */
    CHECK(parse("1970-01-01T00:00:00Z") == 0);
    CHECK(parse("2026-08-14T21:04:11Z") == 1786741451LL);
    CHECK(parse("2000-02-29T12:00:00Z") == 951825600LL);        /* a leap day that is one */
    CHECK(parse("2038-01-19T03:14:08Z") == 2147483648LL);        /* past the 32-bit edge */
    CHECK(parse("0001-01-01T00:00:00Z") == -62135596800LL);      /* DateTimeOffset.MinValue */
    CHECK(parse("9999-12-31T23:59:59Z") == 253402300799LL);      /* DateTimeOffset.MaxValue */
    CHECK(parse("1969-12-31T23:59:59Z") == -1);                  /* negative epochs work (the -1 here IS the value) */

    /* The forms §2 asks readers to accept beyond the canonical one. */
    CHECK(parse("2026-08-14T21:04:11+00:00") == 1786741451LL);
    CHECK(parse("2026-08-14T23:04:11+02:00") == 1786741451LL);
    CHECK(parse("2026-08-14T16:34:11-04:30") == 1786741451LL);
    CHECK(parse("2026-08-14T21:04:11.5Z") == 1786741451LL);       /* fractional seconds ignored */
    CHECK(parse("2026-08-14T21:04:11.1234567+00:00") == 1786741451LL);
    CHECK(parse("2026-08-14t21:04:11z") == 1786741451LL);

    /* Refused. */
    CHECK(mm_time_parse("", &t) == -1);
    CHECK(mm_time_parse(NULL, &t) == -1);
    CHECK(mm_time_parse("2026-08-14 21:04:11Z", &t) == -1);      /* a space, not T */
    CHECK(mm_time_parse("2026-08-14T21:04:11", &t) == -1);       /* no zone */
    CHECK(mm_time_parse("2026-08-14T21:04:11Zx", &t) == -1);
    CHECK(mm_time_parse("2026-02-30T00:00:00Z", &t) == -1);      /* no such day */
    CHECK(mm_time_parse("2025-02-29T00:00:00Z", &t) == -1);      /* not a leap year */
    CHECK(mm_time_parse("2026-13-01T00:00:00Z", &t) == -1);
    CHECK(mm_time_parse("2026-08-14T24:00:00Z", &t) == -1);
    CHECK(mm_time_parse("2026-08-14T21:60:00Z", &t) == -1);
    CHECK(mm_time_parse("2026-08-14T21:04:60Z", &t) == -1);      /* no leap seconds, as in .NET */
    CHECK(mm_time_parse("2026-08-14T21:04:11.Z", &t) == -1);
    CHECK(mm_time_parse("2026-08-14T21:04:11+2:00", &t) == -1);
    CHECK(mm_time_parse("2026-08-14T21:04:11+15:00", &t) == -1);
    CHECK(mm_time_parse("1786741451", &t) == -1);
    CHECK(mm_time_parse("2026-08-14T21:04:1\xc3\xa9", &t) == -1);

    /* Formatting is the canonical form, and round-trips. */
    CHECK(mm_time_format(1786741451LL, out, sizeof out) == 20); CHECK_STR_EQ("2026-08-14T21:04:11Z", out);
    CHECK(mm_time_format(0, out, sizeof out) == 20); CHECK_STR_EQ("1970-01-01T00:00:00Z", out);
    CHECK(mm_time_format(-1, out, sizeof out) == 20); CHECK_STR_EQ("1969-12-31T23:59:59Z", out);
    CHECK(mm_time_format(951825600LL, out, sizeof out) == 20); CHECK_STR_EQ("2000-02-29T12:00:00Z", out);
    CHECK(mm_time_format(253402300799LL, out, sizeof out) == 20); CHECK_STR_EQ("9999-12-31T23:59:59Z", out);
    CHECK(mm_time_format(253402300800LL, out, sizeof out) == 0);  /* year 10000: no canonical form */
    CHECK(mm_time_format(-62135596801LL, out, sizeof out) == 0);  /* before year 1 */
    CHECK(mm_time_format(0, out, 20) == 0);                        /* needs 21 */

    {
        /* Every day boundary across two leap-year cycles round-trips. */
        int64_t day;
        int ok = 1;
        for (day = 10957; day < 10957 + 366 * 8; day++) {
            int64_t secs = day * 86400 + 12 * 3600 + 34 * 60 + 56;
            if (mm_time_format(secs, out, sizeof out) != 20 || parse(out) != secs) ok = 0;
        }
        CHECK(ok);
    }
}
