#include "mm_time.h"

#include <string.h>

/* Days from civil date (Howard Hinnant's algorithm), valid for the whole proleptic Gregorian range. */
static int64_t days_from_civil(int64_t y, unsigned m, unsigned d)
{
    int64_t era, yoe, doy, doe;
    y -= m <= 2;
    era = (y >= 0 ? y : y - 399) / 400;
    yoe = y - era * 400;
    doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + doe - 719468;
}

static void civil_from_days(int64_t z, int64_t *y, unsigned *m, unsigned *d)
{
    int64_t era, doe, yoe, doy, mp;
    z += 719468;
    era = (z >= 0 ? z : z - 146096) / 146097;
    doe = z - era * 146097;
    yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    mp = (5 * doy + 2) / 153;
    *d = (unsigned)(doy - (153 * mp + 2) / 5 + 1);
    *m = (unsigned)(mp < 10 ? mp + 3 : mp - 9);
    *y = yoe + era * 400 + (*m <= 2);
}

static int digits(const char *s, int n, int *out)
{
    int v = 0, i;
    for (i = 0; i < n; i++) {
        if (s[i] < '0' || s[i] > '9') return -1;
        v = v * 10 + (s[i] - '0');
    }
    *out = v;
    return 0;
}

static int days_in_month(int y, int m)
{
    static const int dm[] = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
    if (m == 2 && ((y % 4 == 0 && y % 100 != 0) || y % 400 == 0)) return 29;
    return dm[m - 1];
}

int mm_time_parse(const char *text, int64_t *epoch)
{
    int y, mo, d, h, mi, s, oh = 0, om = 0, sign = 0;
    const char *p;

    if (text == NULL || strlen(text) < 20) return -1;
    if (digits(text, 4, &y) || text[4] != '-' || digits(text + 5, 2, &mo) || text[7] != '-' ||
        digits(text + 8, 2, &d) || (text[10] != 'T' && text[10] != 't') || digits(text + 11, 2, &h) ||
        text[13] != ':' || digits(text + 14, 2, &mi) || text[16] != ':' || digits(text + 17, 2, &s))
        return -1;
    if (mo < 1 || mo > 12 || d < 1 || d > days_in_month(y, mo) || h > 23 || mi > 59 || s > 59) return -1;

    p = text + 19;
    if (*p == '.') {                       /* fractional seconds: accepted, ignored */
        p++;
        if (*p < '0' || *p > '9') return -1;
        while (*p >= '0' && *p <= '9') p++;
    }
    if (*p == 'Z' || *p == 'z') {
        p++;
    } else if (*p == '+' || *p == '-') {
        sign = *p == '-' ? -1 : 1;
        if (strlen(p) < 6 || digits(p + 1, 2, &oh) || p[3] != ':' || digits(p + 4, 2, &om)) return -1;
        if (oh > 14 || om > 59) return -1;
        p += 6;
    } else {
        return -1;
    }
    if (*p != '\0') return -1;

    *epoch = days_from_civil(y, (unsigned)mo, (unsigned)d) * 86400 + h * 3600 + mi * 60 + s
           - sign * (oh * 3600 + om * 60);
    return 0;
}

size_t mm_time_format(int64_t epoch, char *out, size_t cap)
{
    int64_t days = epoch / 86400, rem = epoch % 86400, y;
    unsigned m, d;
    int h, mi, s, i;
    char *o = out;

    if (cap < MM_TIME_TEXT_LEN + 1) return 0;
    if (rem < 0) { rem += 86400; days -= 1; }
    civil_from_days(days, &y, &m, &d);
    if (y < 1 || y > 9999) return 0;
    h = (int)(rem / 3600); mi = (int)(rem % 3600 / 60); s = (int)(rem % 60);

    for (i = 3; i >= 0; i--) { o[i] = (char)('0' + y % 10); y /= 10; }
    o[4] = '-'; o[5] = (char)('0' + m / 10); o[6] = (char)('0' + m % 10);
    o[7] = '-'; o[8] = (char)('0' + d / 10); o[9] = (char)('0' + d % 10);
    o[10] = 'T'; o[11] = (char)('0' + h / 10); o[12] = (char)('0' + h % 10);
    o[13] = ':'; o[14] = (char)('0' + mi / 10); o[15] = (char)('0' + mi % 10);
    o[16] = ':'; o[17] = (char)('0' + s / 10); o[18] = (char)('0' + s % 10);
    o[19] = 'Z'; o[20] = '\0';
    return MM_TIME_TEXT_LEN;
}
