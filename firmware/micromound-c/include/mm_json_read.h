/*
 * mm_json_read — a bounded JSON reader for what a constrained device receives.
 *
 * A pull parser over a byte buffer: the caller walks the document — begin an object, take the
 * next key, read a value of the type the contract says, skip what it does not know — and nothing
 * is allocated, copied or tokenized ahead of time. Strings are decoded into caller buffers with
 * the full escape grammar (\uXXXX, surrogate pairs) and rejected when they are not valid UTF-8 or
 * not valid JSON; numbers are checked against the JSON grammar before strtod; nesting is bounded.
 *
 * It accepts more than the canonical form — insignificant whitespace, any member order, unknown
 * members (PROTOCOL.md §11: additive fields are always legal) — and nothing outside RFC 8259.
 * Every failure is a distinct error code and the reader stops at the first one.
 */
#ifndef MM_JSON_READ_H
#define MM_JSON_READ_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MM_JR_MAX_DEPTH 16

enum mm_jr_error {
    MM_JR_OK = 0,
    MM_JR_SYNTAX,      /* not JSON: bad token, bad escape, control character in a string, trailing comma… */
    MM_JR_TYPE,        /* a value of another type than the contract's */
    MM_JR_DEPTH,       /* nesting beyond MM_JR_MAX_DEPTH */
    MM_JR_OVERFLOW,    /* a decoded string longer than the caller's buffer */
    MM_JR_TOO_MANY,    /* more array elements or object members than the caller can hold */
    MM_JR_BAD_UTF8,    /* invalid UTF-8 in a string, or an escape that decodes to a lone surrogate */
    MM_JR_RANGE,       /* a number outside the type's range (an integer that is not one, or too large) */
    MM_JR_TRAILING,    /* bytes after the document */
    MM_JR_TRUNCATED    /* the input ended inside a value */
};

typedef struct mm_jr {
    const char *p;
    const char *end;
    int error;         /* enum mm_jr_error; sticky */
    int depth;
    unsigned seen;     /* bit d set: a member has been consumed at depth d */
} mm_jr;

void mm_jr_init(mm_jr *r, const char *json, size_t n);

/* What the next value is: '{', '[', '"', 'n' (null), 'b' (true/false), '0' (number), or 0 on error. */
int mm_jr_peek(mm_jr *r);

/* Objects: begin, then next() until it returns 0. next() returns 1 and the decoded key, 0 at '}', -1 on error. */
int mm_jr_object_begin(mm_jr *r);
int mm_jr_object_next(mm_jr *r, char *key, size_t key_cap);

/* Arrays: begin, then next() until it returns 0. next() returns 1 when a value follows, 0 at ']', -1 on error. */
int mm_jr_array_begin(mm_jr *r);
int mm_jr_array_next(mm_jr *r);

/* Scalars. Each returns 0 on success, -1 with r->error set otherwise. */
int mm_jr_string(mm_jr *r, char *out, size_t cap);   /* decoded UTF-8, NUL-terminated */
int mm_jr_double(mm_jr *r, double *out);
int mm_jr_int(mm_jr *r, long long *out);             /* integer grammar only: no fraction, no exponent */
int mm_jr_bool(mm_jr *r, int *out);
int mm_jr_null(mm_jr *r);

/* Any value, consumed and discarded (validated as it is skipped). */
int mm_jr_skip(mm_jr *r);

/* Any value, consumed; start and n delimit its exact source bytes. */
int mm_jr_raw(mm_jr *r, const char **start, size_t *n);

/* After the root value: nothing but whitespace may remain. */
int mm_jr_end(mm_jr *r);

/* The error name, for audit lines. */
const char *mm_jr_error_name(int error);

#ifdef __cplusplus
}
#endif

#endif
