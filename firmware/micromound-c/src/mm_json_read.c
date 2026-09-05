#include "mm_json_read.h"
#include "mm_json.h"

#include <errno.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

static int fail(mm_jr *r, int error)
{
    if (r->error == MM_JR_OK) r->error = error;
    return -1;
}

static void skip_ws(mm_jr *r)
{
    while (r->p < r->end && (*r->p == ' ' || *r->p == '\t' || *r->p == '\n' || *r->p == '\r')) r->p++;
}

static int at_end(mm_jr *r)
{
    return r->p >= r->end;
}

void mm_jr_init(mm_jr *r, const char *json, size_t n)
{
    memset(r, 0, sizeof *r);
    r->p = json;
    r->end = json + n;
}

int mm_jr_peek(mm_jr *r)
{
    if (r->error) return 0;
    skip_ws(r);
    if (at_end(r)) { fail(r, MM_JR_TRUNCATED); return 0; }
    switch (*r->p) {
        case '{': case '[': case '"': return *r->p;
        case 'n': return 'n';
        case 't': case 'f': return 'b';
        case '-': case '0': case '1': case '2': case '3': case '4':
        case '5': case '6': case '7': case '8': case '9': return '0';
        default: fail(r, MM_JR_SYNTAX); return 0;
    }
}

/* ---- containers -------------------------------------------------------------------------- */

#define SEEN_BIT(d) (1u << (d))

static int begin(mm_jr *r, char open)
{
    int d;
    if (r->error) return -1;
    skip_ws(r);
    if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
    if (*r->p != open) return fail(r, *r->p == '{' || *r->p == '[' || *r->p == '"' || *r->p == 'n' || *r->p == 't' || *r->p == 'f' || *r->p == '-' || (*r->p >= '0' && *r->p <= '9') ? MM_JR_TYPE : MM_JR_SYNTAX);
    d = r->depth;
    if (d >= MM_JR_MAX_DEPTH) return fail(r, MM_JR_DEPTH);
    r->p++;
    r->depth = d + 1;
    r->seen &= ~SEEN_BIT(d + 1);
    return 0;
}

/* Positions on the next element or member: 1 = one follows, 0 = the container closed, -1 = error. */
static int next(mm_jr *r, char close)
{
    int d;
    if (r->error) return -1;
    d = r->depth;
    if (d == 0) return fail(r, MM_JR_SYNTAX);
    skip_ws(r);
    if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
    if (*r->p == close) {
        r->p++;
        r->seen &= ~SEEN_BIT(d);
        r->depth = d - 1;
        return 0;
    }
    if (r->seen & SEEN_BIT(d)) {
        if (*r->p != ',') return fail(r, MM_JR_SYNTAX);
        r->p++;
        skip_ws(r);
        if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
        if (*r->p == close) return fail(r, MM_JR_SYNTAX);   /* trailing comma */
    }
    r->seen |= SEEN_BIT(d);
    return 1;
}

int mm_jr_object_begin(mm_jr *r) { return begin(r, '{'); }
int mm_jr_array_begin(mm_jr *r) { return begin(r, '['); }
int mm_jr_array_next(mm_jr *r) { return next(r, ']'); }

int mm_jr_object_next(mm_jr *r, char *key, size_t key_cap)
{
    int more = next(r, '}');
    if (more != 1) return more;
    skip_ws(r);
    if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
    if (*r->p != '"') return fail(r, MM_JR_SYNTAX);
    if (mm_jr_string(r, key, key_cap) != 0) return -1;
    skip_ws(r);
    if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
    if (*r->p != ':') return fail(r, MM_JR_SYNTAX);
    r->p++;
    return 1;
}

/* ---- strings ----------------------------------------------------------------------------- */

static int hex4(const char *s, unsigned *out)
{
    unsigned v = 0;
    int i;
    for (i = 0; i < 4; i++) {
        char c = s[i];
        v <<= 4;
        if (c >= '0' && c <= '9') v |= (unsigned)(c - '0');
        else if (c >= 'a' && c <= 'f') v |= (unsigned)(c - 'a' + 10);
        else if (c >= 'A' && c <= 'F') v |= (unsigned)(c - 'A' + 10);
        else return -1;
    }
    *out = v;
    return 0;
}

/* Appends up to 4 bytes; out may be NULL to measure/skip. */
static int put(char *out, size_t cap, size_t *len, const char *bytes, size_t n)
{
    if (out) {
        if (*len + n + 1 > cap) return -1;   /* keep room for the NUL */
        memcpy(out + *len, bytes, n);
    }
    *len += n;
    return 0;
}

/* Decodes the string literal at r->p (which must be '"'). out NULL = validate and skip. */
static int read_string(mm_jr *r, char *out, size_t cap)
{
    size_t len = 0;
    char enc[4];

    if (r->error) return -1;
    skip_ws(r);
    if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
    if (*r->p != '"') return fail(r, mm_jr_peek(r) ? MM_JR_TYPE : MM_JR_SYNTAX);
    r->p++;

    for (;;) {
        unsigned char c;
        if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
        c = (unsigned char)*r->p;

        if (c == '"') {
            r->p++;
            if (out) out[len] = '\0';
            return 0;
        }
        if (c < 0x20) return fail(r, MM_JR_SYNTAX);           /* raw control character */

        if (c == '\\') {
            unsigned long cp;
            char e;
            r->p++;
            if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
            e = *r->p++;
            switch (e) {
                case '"': cp = '"'; break;
                case '\\': cp = '\\'; break;
                case '/': cp = '/'; break;
                case 'b': cp = 0x08; break;
                case 'f': cp = 0x0C; break;
                case 'n': cp = 0x0A; break;
                case 'r': cp = 0x0D; break;
                case 't': cp = 0x09; break;
                case 'u': {
                    unsigned unit;
                    if (r->end - r->p < 4) return fail(r, MM_JR_TRUNCATED);
                    if (hex4(r->p, &unit) != 0) return fail(r, MM_JR_SYNTAX);
                    r->p += 4;
                    if (unit >= 0xD800 && unit <= 0xDBFF) {
                        unsigned low;
                        /* a high surrogate must be followed by an escaped low one; anything else is a lone surrogate */
                        if (r->end - r->p >= 1 && r->p[0] != '\\') return fail(r, MM_JR_BAD_UTF8);
                        if (r->end - r->p >= 2 && r->p[1] != 'u') return fail(r, MM_JR_BAD_UTF8);
                        if (r->end - r->p < 6) return fail(r, MM_JR_TRUNCATED);
                        if (hex4(r->p + 2, &low) != 0) return fail(r, MM_JR_SYNTAX);
                        if (low < 0xDC00 || low > 0xDFFF) return fail(r, MM_JR_BAD_UTF8);
                        r->p += 6;
                        cp = 0x10000 + (((unsigned long)unit - 0xD800) << 10) + (low - 0xDC00);
                    } else if (unit >= 0xDC00 && unit <= 0xDFFF) {
                        return fail(r, MM_JR_BAD_UTF8);       /* a lone low surrogate */
                    } else {
                        cp = unit;
                    }
                    break;
                }
                default: return fail(r, MM_JR_SYNTAX);
            }
            if (put(out, cap, &len, enc, mm_utf8_encode(cp, enc)) != 0) return fail(r, MM_JR_OVERFLOW);
            continue;
        }

        {
            unsigned long cp;
            size_t used = mm_utf8_decode((const unsigned char *)r->p, (size_t)(r->end - r->p), &cp);
            if (used == 0) return fail(r, MM_JR_BAD_UTF8);
            if (put(out, cap, &len, r->p, used) != 0) return fail(r, MM_JR_OVERFLOW);
            r->p += used;
        }
    }
}

int mm_jr_string(mm_jr *r, char *out, size_t cap)
{
    if (cap == 0) return fail(r, MM_JR_OVERFLOW);
    return read_string(r, out, cap);
}

/* ---- numbers ----------------------------------------------------------------------------- */

/* Validates the JSON number grammar at r->p; returns its length or 0. *is_integer: no '.', 'e', 'E'. */
static size_t scan_number(mm_jr *r, int *is_integer)
{
    const char *s = r->p, *p = s;
    *is_integer = 1;
    if (p < r->end && *p == '-') p++;
    if (p >= r->end) return 0;
    if (*p == '0') p++;
    else if (*p >= '1' && *p <= '9') { while (p < r->end && *p >= '0' && *p <= '9') p++; }
    else return 0;
    if (p < r->end && *p == '.') {
        *is_integer = 0;
        p++;
        if (p >= r->end || *p < '0' || *p > '9') return 0;
        while (p < r->end && *p >= '0' && *p <= '9') p++;
    }
    if (p < r->end && (*p == 'e' || *p == 'E')) {
        *is_integer = 0;
        p++;
        if (p < r->end && (*p == '+' || *p == '-')) p++;
        if (p >= r->end || *p < '0' || *p > '9') return 0;
        while (p < r->end && *p >= '0' && *p <= '9') p++;
    }
    return (size_t)(p - s);
}

static int number_start(mm_jr *r)
{
    if (r->error) return -1;
    skip_ws(r);
    if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
    if (*r->p != '-' && (*r->p < '0' || *r->p > '9')) return fail(r, mm_jr_peek(r) ? MM_JR_TYPE : MM_JR_SYNTAX);
    return 0;
}

int mm_jr_double(mm_jr *r, double *out)
{
    char text[64];
    int is_integer;
    size_t n;
    if (number_start(r) != 0) return -1;
    n = scan_number(r, &is_integer);
    if (n == 0) return fail(r, MM_JR_SYNTAX);
    if (n >= sizeof text) return fail(r, MM_JR_RANGE);
    memcpy(text, r->p, n);
    text[n] = '\0';
    errno = 0;
    *out = strtod(text, NULL);
    if (!isfinite(*out)) return fail(r, MM_JR_RANGE);
    r->p += n;
    return 0;
}

int mm_jr_int(mm_jr *r, long long *out)
{
    char text[32];
    int is_integer;
    size_t n;
    if (number_start(r) != 0) return -1;
    n = scan_number(r, &is_integer);
    if (n == 0) return fail(r, MM_JR_SYNTAX);
    if (!is_integer) return fail(r, MM_JR_RANGE);
    if (n >= sizeof text) return fail(r, MM_JR_RANGE);
    memcpy(text, r->p, n);
    text[n] = '\0';
    errno = 0;
    *out = strtoll(text, NULL, 10);
    if (errno == ERANGE) return fail(r, MM_JR_RANGE);
    r->p += n;
    return 0;
}

/* ---- literals ---------------------------------------------------------------------------- */

static int literal(mm_jr *r, const char *word)
{
    size_t n = strlen(word);
    if ((size_t)(r->end - r->p) < n) return fail(r, MM_JR_TRUNCATED);
    if (memcmp(r->p, word, n) != 0) return fail(r, MM_JR_SYNTAX);
    r->p += n;
    return 0;
}

int mm_jr_bool(mm_jr *r, int *out)
{
    if (r->error) return -1;
    skip_ws(r);
    if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
    if (*r->p == 't') { *out = 1; return literal(r, "true"); }
    if (*r->p == 'f') { *out = 0; return literal(r, "false"); }
    return fail(r, mm_jr_peek(r) ? MM_JR_TYPE : MM_JR_SYNTAX);
}

int mm_jr_null(mm_jr *r)
{
    if (r->error) return -1;
    skip_ws(r);
    if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
    if (*r->p != 'n') return fail(r, mm_jr_peek(r) ? MM_JR_TYPE : MM_JR_SYNTAX);
    return literal(r, "null");
}

/* ---- skipping ---------------------------------------------------------------------------- */

int mm_jr_skip(mm_jr *r)
{
    int kind = mm_jr_peek(r);
    switch (kind) {
        case '{': {
            if (mm_jr_object_begin(r) != 0) return -1;
            for (;;) {
                int more = next(r, '}');                     /* the key is validated, not kept */
                if (more < 0) return -1;
                if (more == 0) return 0;
                skip_ws(r);
                if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
                if (*r->p != '"') return fail(r, MM_JR_SYNTAX);
                if (read_string(r, NULL, 0) != 0) return -1;
                skip_ws(r);
                if (at_end(r)) return fail(r, MM_JR_TRUNCATED);
                if (*r->p != ':') return fail(r, MM_JR_SYNTAX);
                r->p++;
                if (mm_jr_skip(r) != 0) return -1;
            }
        }
        case '[':
            if (mm_jr_array_begin(r) != 0) return -1;
            for (;;) {
                int more = mm_jr_array_next(r);
                if (more < 0) return -1;
                if (more == 0) return 0;
                if (mm_jr_skip(r) != 0) return -1;
            }
        case '"': return read_string(r, NULL, 0);
        case '0': { double d; return mm_jr_double(r, &d); }
        case 'b': { int b; return mm_jr_bool(r, &b); }
        case 'n': return mm_jr_null(r);
        default: return -1;
    }
}

int mm_jr_raw(mm_jr *r, const char **start, size_t *n)
{
    if (mm_jr_peek(r) == 0) return -1;
    *start = r->p;
    if (mm_jr_skip(r) != 0) return -1;
    *n = (size_t)(r->p - *start);
    return 0;
}

int mm_jr_end(mm_jr *r)
{
    if (r->error) return -1;
    if (r->depth != 0) return fail(r, MM_JR_SYNTAX);
    skip_ws(r);
    if (!at_end(r)) return fail(r, MM_JR_TRAILING);
    return 0;
}

const char *mm_jr_error_name(int error)
{
    switch (error) {
        case MM_JR_OK: return "ok";
        case MM_JR_SYNTAX: return "syntax";
        case MM_JR_TYPE: return "type";
        case MM_JR_DEPTH: return "depth";
        case MM_JR_OVERFLOW: return "overflow";
        case MM_JR_TOO_MANY: return "too_many";
        case MM_JR_BAD_UTF8: return "bad_utf8";
        case MM_JR_RANGE: return "range";
        case MM_JR_TRAILING: return "trailing";
        case MM_JR_TRUNCATED: return "truncated";
        default: return "unknown";
    }
}
