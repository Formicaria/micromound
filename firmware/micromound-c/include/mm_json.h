/*
 * mm_json — the canonical JSON writer of the MICROMOUND wire format, in C.
 *
 * Writes exactly the bytes System.Text.Json produces under ProtocolJson.Options
 * (src/Micromound.Protocol/Envelope.cs): no whitespace, every field present, strings escaped by
 * the ASCII-only rule of PROTOCOL.md §2, numbers formatted by mm_format. It writes into a
 * caller-supplied buffer and never allocates. On overflow it keeps counting so the caller can
 * learn the size it needed; the output is then invalid and mm_json_ok() says so.
 *
 * Fixtures: tests/Micromound.Tests/Golden/files/canonical-strings.txt pins the escaping;
 * canonical-envelopes.txt and canonical-bodies.txt pin whole documents.
 */
#ifndef MM_JSON_H
#define MM_JSON_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Nesting depth the writer tracks. The deepest protocol document (a charter's limits table) is 4. */
#define MM_JSON_MAX_DEPTH 16

enum mm_json_error {
    MM_JSON_OK = 0,
    MM_JSON_OVERFLOW,   /* the buffer was too small; len holds the length that was needed */
    MM_JSON_DEPTH,      /* nesting deeper than MM_JSON_MAX_DEPTH, or an end without a begin */
    MM_JSON_BAD_UTF8,   /* a string was not valid UTF-8 (C# strings never are; the C side must not be either) */
    MM_JSON_NONFINITE   /* NaN or an infinity, which the wire format cannot carry */
};

typedef struct mm_json {
    char *buf;
    size_t cap;
    size_t len;                             /* bytes written, or needed once overflowed */
    int error;                              /* enum mm_json_error; sticky */
    int depth;
    unsigned char in_array[MM_JSON_MAX_DEPTH + 1];
    unsigned char first[MM_JSON_MAX_DEPTH + 1];
} mm_json;

void mm_json_init(mm_json *w, char *buf, size_t cap);

void mm_json_object_begin(mm_json *w);
void mm_json_object_end(mm_json *w);
void mm_json_array_begin(mm_json *w);
void mm_json_array_end(mm_json *w);

/* A property name (escaped by the same rule as values) followed by ':'. */
void mm_json_key(mm_json *w, const char *name);

void mm_json_string(mm_json *w, const char *utf8);                 /* NUL-terminated */
void mm_json_string_n(mm_json *w, const char *utf8, size_t n);     /* may contain NUL */
void mm_json_int(mm_json *w, long long value);                     /* C# int / long */
void mm_json_double(mm_json *w, double value);                     /* C# double — see mm_format.h */
void mm_json_bool(mm_json *w, int value);
void mm_json_null(mm_json *w);

/* Pre-encoded JSON, copied verbatim as one value. The caller vouches for it being canonical. */
void mm_json_raw(mm_json *w, const char *json, size_t n);

/* Convenience: key + value. */
void mm_json_kv_string(mm_json *w, const char *key, const char *utf8);
void mm_json_kv_int(mm_json *w, const char *key, long long value);
void mm_json_kv_double(mm_json *w, const char *key, double value);
void mm_json_kv_bool(mm_json *w, const char *key, int value);
void mm_json_kv_null(mm_json *w, const char *key);

/* Non-zero when nothing went wrong and every begin was ended. */
int mm_json_ok(const mm_json *w);

/*
 * Terminates the buffer with a NUL when there is room (the NUL is not counted in len) and
 * returns len, or 0 if the document is not ok. A document longer than cap returns 0 and leaves
 * the needed length in w->len.
 */
size_t mm_json_finish(mm_json *w);

/*
 * The escaping rule on its own: writes the JSON string literal (with quotes) for n bytes of
 * UTF-8 into out, returns its length, or 0 with *error set on bad UTF-8 or overflow. Used by the
 * writer and exposed for tests and for callers that only need one literal.
 */
size_t mm_json_escape(const char *utf8, size_t n, char *out, size_t cap, int *error);

#ifdef __cplusplus
}
#endif

#endif
