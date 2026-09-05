#include "mm_json.h"
#include "mm_format.h"

#include <string.h>

/* ---- raw output -------------------------------------------------------------------------- */

static void put_bytes(mm_json *w, const char *bytes, size_t n)
{
    if (w->len + n <= w->cap)
        memcpy(w->buf + w->len, bytes, n);
    else if (w->error == MM_JSON_OK)
        w->error = MM_JSON_OVERFLOW;
    w->len += n;
}

static void put_char(mm_json *w, char c)
{
    put_bytes(w, &c, 1);
}

/* ---- the escaping rule (PROTOCOL.md §2) -------------------------------------------------- */

static const char HEX_UPPER[] = "0123456789ABCDEF";

static size_t put_u16_escape(char *out, unsigned unit)
{
    out[0] = '\\';
    out[1] = 'u';
    out[2] = HEX_UPPER[(unit >> 12) & 0xF];
    out[3] = HEX_UPPER[(unit >> 8) & 0xF];
    out[4] = HEX_UPPER[(unit >> 4) & 0xF];
    out[5] = HEX_UPPER[unit & 0xF];
    return 6;
}

/*
 * Decodes one UTF-8 scalar at s[0..n). Returns the number of bytes consumed, or 0 for invalid
 * input: bad lead byte, truncated sequence, overlong form, surrogate, or above U+10FFFF — the
 * same set every strict decoder rejects, so the C side cannot canonicalize a string the host
 * could not have held.
 */
static size_t decode_utf8(const unsigned char *s, size_t n, unsigned long *scalar)
{
    unsigned char b0 = s[0];
    if (b0 < 0x80) { *scalar = b0; return 1; }
    if (b0 < 0xC2) return 0;                             /* continuation byte or overlong 2-byte lead */
    if (b0 < 0xE0) {
        if (n < 2 || (s[1] & 0xC0) != 0x80) return 0;
        *scalar = ((unsigned long)(b0 & 0x1F) << 6) | (s[1] & 0x3F);
        return 2;
    }
    if (b0 < 0xF0) {
        if (n < 3 || (s[1] & 0xC0) != 0x80 || (s[2] & 0xC0) != 0x80) return 0;
        *scalar = ((unsigned long)(b0 & 0x0F) << 12) | ((unsigned long)(s[1] & 0x3F) << 6) | (s[2] & 0x3F);
        if (*scalar < 0x800) return 0;                    /* overlong */
        if (*scalar >= 0xD800 && *scalar <= 0xDFFF) return 0;
        return 3;
    }
    if (b0 < 0xF5) {
        if (n < 4 || (s[1] & 0xC0) != 0x80 || (s[2] & 0xC0) != 0x80 || (s[3] & 0xC0) != 0x80) return 0;
        *scalar = ((unsigned long)(b0 & 0x07) << 18) | ((unsigned long)(s[1] & 0x3F) << 12)
                | ((unsigned long)(s[2] & 0x3F) << 6) | (s[3] & 0x3F);
        if (*scalar < 0x10000 || *scalar > 0x10FFFF) return 0;
        return 4;
    }
    return 0;
}

/* Escapes one scalar into out (at least 12 bytes). Returns the length. */
static size_t escape_scalar(unsigned long cp, char *out)
{
    switch (cp) {
        case '"':  out[0] = '\\'; out[1] = '"';  return 2;
        case '\\': out[0] = '\\'; out[1] = '\\'; return 2;
        case 0x08: out[0] = '\\'; out[1] = 'b';  return 2;
        case 0x09: out[0] = '\\'; out[1] = 't';  return 2;
        case 0x0A: out[0] = '\\'; out[1] = 'n';  return 2;
        case 0x0C: out[0] = '\\'; out[1] = 'f';  return 2;
        case 0x0D: out[0] = '\\'; out[1] = 'r';  return 2;
        default: break;
    }
    if (cp >= 0x20 && cp < 0x7F) { out[0] = (char)cp; return 1; }
    if (cp <= 0xFFFF) return put_u16_escape(out, (unsigned)cp);
    {
        unsigned long v = cp - 0x10000;
        size_t k = put_u16_escape(out, (unsigned)(0xD800 + (v >> 10)));
        return k + put_u16_escape(out + k, (unsigned)(0xDC00 + (v & 0x3FF)));
    }
}

static void put_string_literal(mm_json *w, const char *utf8, size_t n)
{
    const unsigned char *s = (const unsigned char *)utf8;
    size_t i = 0;
    char piece[12];

    put_char(w, '"');
    while (i < n) {
        unsigned long cp;
        size_t used = decode_utf8(s + i, n - i, &cp);
        if (used == 0) {
            if (w->error == MM_JSON_OK) w->error = MM_JSON_BAD_UTF8;
            return;
        }
        put_bytes(w, piece, escape_scalar(cp, piece));
        i += used;
    }
    put_char(w, '"');
}

size_t mm_json_escape(const char *utf8, size_t n, char *out, size_t cap, int *error)
{
    mm_json w;
    mm_json_init(&w, out, cap);
    put_string_literal(&w, utf8, n);
    if (error) *error = w.error;
    if (w.error != MM_JSON_OK) return 0;
    if (w.len < cap) out[w.len] = '\0';
    return w.len;
}

/* ---- structure --------------------------------------------------------------------------- */

void mm_json_init(mm_json *w, char *buf, size_t cap)
{
    memset(w, 0, sizeof *w);
    w->buf = buf;
    w->cap = cap;
    w->first[0] = 1;
}

/* Before a value (or a key): the comma, when this is not the first member of the container. */
static void before_member(mm_json *w)
{
    if (!w->first[w->depth]) put_char(w, ',');
    w->first[w->depth] = 0;
}

/* Before a VALUE: a comma only inside an array; inside an object the key already placed it. */
static void before_value(mm_json *w)
{
    if (w->depth == 0 || w->in_array[w->depth]) before_member(w);
}

static void begin(mm_json *w, int is_array)
{
    before_value(w);
    if (w->depth >= MM_JSON_MAX_DEPTH) {
        if (w->error == MM_JSON_OK) w->error = MM_JSON_DEPTH;
        return;
    }
    w->depth++;
    w->in_array[w->depth] = (unsigned char)is_array;
    w->first[w->depth] = 1;
    put_char(w, is_array ? '[' : '{');
}

static void end(mm_json *w, int is_array)
{
    if (w->depth == 0 || (int)w->in_array[w->depth] != is_array) {
        if (w->error == MM_JSON_OK) w->error = MM_JSON_DEPTH;
        return;
    }
    w->depth--;
    put_char(w, is_array ? ']' : '}');
}

void mm_json_object_begin(mm_json *w) { begin(w, 0); }
void mm_json_object_end(mm_json *w) { end(w, 0); }
void mm_json_array_begin(mm_json *w) { begin(w, 1); }
void mm_json_array_end(mm_json *w) { end(w, 1); }

void mm_json_key(mm_json *w, const char *name)
{
    if (w->depth == 0 || w->in_array[w->depth]) {
        if (w->error == MM_JSON_OK) w->error = MM_JSON_DEPTH;   /* a key outside an object */
        return;
    }
    before_member(w);
    put_string_literal(w, name, strlen(name));
    put_char(w, ':');
}

void mm_json_string_n(mm_json *w, const char *utf8, size_t n)
{
    before_value(w);
    put_string_literal(w, utf8, n);
}

void mm_json_string(mm_json *w, const char *utf8)
{
    mm_json_string_n(w, utf8, strlen(utf8));
}

void mm_json_int(mm_json *w, long long value)
{
    char text[24];
    size_t n = mm_format_int(value, text, sizeof text);
    before_value(w);
    put_bytes(w, text, n);
}

void mm_json_double(mm_json *w, double value)
{
    char text[MM_FORMAT_DOUBLE_MAX];
    size_t n = mm_format_double(value, text, sizeof text);
    before_value(w);
    if (n == 0) {
        if (w->error == MM_JSON_OK) w->error = MM_JSON_NONFINITE;
        return;
    }
    put_bytes(w, text, n);
}

void mm_json_bool(mm_json *w, int value)
{
    before_value(w);
    if (value) put_bytes(w, "true", 4); else put_bytes(w, "false", 5);
}

void mm_json_null(mm_json *w)
{
    before_value(w);
    put_bytes(w, "null", 4);
}

void mm_json_raw(mm_json *w, const char *json, size_t n)
{
    before_value(w);
    put_bytes(w, json, n);
}

void mm_json_kv_string(mm_json *w, const char *key, const char *utf8) { mm_json_key(w, key); mm_json_string(w, utf8); }
void mm_json_kv_int(mm_json *w, const char *key, long long value) { mm_json_key(w, key); mm_json_int(w, value); }
void mm_json_kv_double(mm_json *w, const char *key, double value) { mm_json_key(w, key); mm_json_double(w, value); }
void mm_json_kv_bool(mm_json *w, const char *key, int value) { mm_json_key(w, key); mm_json_bool(w, value); }
void mm_json_kv_null(mm_json *w, const char *key) { mm_json_key(w, key); mm_json_null(w); }

int mm_json_ok(const mm_json *w)
{
    return w->error == MM_JSON_OK && w->depth == 0;
}

size_t mm_json_finish(mm_json *w)
{
    if (!mm_json_ok(w)) return 0;
    if (w->len < w->cap) w->buf[w->len] = '\0';
    return w->len;
}
