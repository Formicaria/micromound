#include "mm_test.h"
#include "mm_json_read.h"

#include <string.h>

/* Reads one string document; returns the error code (MM_JR_OK on success with out filled). */
static int read_str_doc(const char *json, char *out, size_t cap)
{
    mm_jr r;
    mm_jr_init(&r, json, strlen(json));
    if (mm_jr_string(&r, out, cap) == 0) mm_jr_end(&r);
    return r.error;
}

static int skip_doc(const char *json)
{
    mm_jr r;
    mm_jr_init(&r, json, strlen(json));
    if (mm_jr_skip(&r) == 0) mm_jr_end(&r);
    return r.error;
}

void test_json_read(void)
{
    char s[64];
    mm_jr r;

    /* Strings: every escape, both surrogate halves, raw UTF-8, and the canonical writer's output. */
    CHECK(read_str_doc("\"plain\"", s, sizeof s) == MM_JR_OK); CHECK_STR_EQ("plain", s);
    CHECK(read_str_doc("\"a\\\"b\\\\c\\/d\"", s, sizeof s) == MM_JR_OK); CHECK_STR_EQ("a\"b\\c/d", s);
    CHECK(read_str_doc("\"\\b\\f\\n\\r\\t\"", s, sizeof s) == MM_JR_OK); CHECK_STR_EQ("\b\f\n\r\t", s);
    CHECK(read_str_doc("\"\\u00E9\\u6f22\\u5B57\"", s, sizeof s) == MM_JR_OK); CHECK_STR_EQ("\xc3\xa9\xe6\xbc\xa2\xe5\xad\x97", s);
    CHECK(read_str_doc("\"\\uD83D\\uDE00\"", s, sizeof s) == MM_JR_OK); CHECK_STR_EQ("\xf0\x9f\x98\x80", s);
    CHECK(read_str_doc("\"\\u0000x\"", s, sizeof s) == MM_JR_OK); CHECK(s[0] == 0 && s[1] == 'x' && s[2] == 0);
    CHECK(read_str_doc("\"raw \xc3\xa9 \xf0\x9f\x98\x80\"", s, sizeof s) == MM_JR_OK); CHECK_STR_EQ("raw \xc3\xa9 \xf0\x9f\x98\x80", s);
    CHECK(read_str_doc("  \"ws\"  ", s, sizeof s) == MM_JR_OK); CHECK_STR_EQ("ws", s);

    /* Strings: what is refused, and why. */
    CHECK(read_str_doc("\"\\uD83D\"", s, sizeof s) == MM_JR_BAD_UTF8);          /* lone high surrogate */
    CHECK(read_str_doc("\"\\uDE00\"", s, sizeof s) == MM_JR_BAD_UTF8);          /* lone low surrogate */
    CHECK(read_str_doc("\"\\uD83Dx\"", s, sizeof s) == MM_JR_BAD_UTF8);
    CHECK(read_str_doc("\"\\uD83D\\u0041\"", s, sizeof s) == MM_JR_BAD_UTF8);   /* high surrogate, non-low follower */
    CHECK(read_str_doc("\"\\x41\"", s, sizeof s) == MM_JR_SYNTAX);              /* unknown escape */
    CHECK(read_str_doc("\"\\u12G4\"", s, sizeof s) == MM_JR_SYNTAX);
    CHECK(read_str_doc("\"tab\there\"", s, sizeof s) == MM_JR_SYNTAX);         /* raw control character */
    CHECK(read_str_doc("\"\xc3\"", s, sizeof s) == MM_JR_BAD_UTF8);              /* truncated UTF-8 */
    CHECK(read_str_doc("\"\xed\xa0\x80\"", s, sizeof s) == MM_JR_BAD_UTF8);      /* encoded surrogate */
    CHECK(read_str_doc("\"\xc0\x80\"", s, sizeof s) == MM_JR_BAD_UTF8);          /* overlong */
    CHECK(read_str_doc("\"unterminated", s, sizeof s) == MM_JR_TRUNCATED);
    CHECK(read_str_doc("\"\\u12", s, sizeof s) == MM_JR_TRUNCATED);
    CHECK(read_str_doc("\"too long for eight\"", s, 8) == MM_JR_OVERFLOW);
    CHECK(read_str_doc("\"exactly7\"", s, 8) == MM_JR_OVERFLOW);               /* the NUL needs its byte */
    CHECK(read_str_doc("\"exactl7\"", s, 8) == MM_JR_OK);
    CHECK(read_str_doc("\"\\u00E9\"", s, 3) == MM_JR_OK);                       /* 2 bytes + NUL: counted in bytes */
    CHECK(read_str_doc("\"\\u00E9\"", s, 2) == MM_JR_OVERFLOW);
    CHECK(read_str_doc("42", s, sizeof s) == MM_JR_TYPE);
    CHECK(read_str_doc("\"a\" \"b\"", s, sizeof s) == MM_JR_TRAILING);

    /* Numbers. */
    {
        double d; long long i;
        mm_jr_init(&r, "-0.5e+2", 7); CHECK(mm_jr_double(&r, &d) == 0 && d == -50.0);
        mm_jr_init(&r, "30", 2); CHECK(mm_jr_double(&r, &d) == 0 && d == 30.0);
        mm_jr_init(&r, "1E+21", 5); CHECK(mm_jr_double(&r, &d) == 0 && d == 1e21);
        mm_jr_init(&r, "-0", 2); CHECK(mm_jr_double(&r, &d) == 0 && d == 0.0);
        mm_jr_init(&r, "900", 3); CHECK(mm_jr_int(&r, &i) == 0 && i == 900);
        mm_jr_init(&r, "-1", 2); CHECK(mm_jr_int(&r, &i) == 0 && i == -1);
        mm_jr_init(&r, "9223372036854775807", 19); CHECK(mm_jr_int(&r, &i) == 0 && i == 9223372036854775807LL);
        mm_jr_init(&r, "9223372036854775808", 19); CHECK(mm_jr_int(&r, &i) != 0 && r.error == MM_JR_RANGE);
        mm_jr_init(&r, "1.0", 3); CHECK(mm_jr_int(&r, &i) != 0 && r.error == MM_JR_RANGE);       /* an int is an int */
        mm_jr_init(&r, "1e2", 3); CHECK(mm_jr_int(&r, &i) != 0 && r.error == MM_JR_RANGE);
        mm_jr_init(&r, "01", 2); CHECK(mm_jr_double(&r, &d) == 0 && mm_jr_end(&r) != 0 && r.error == MM_JR_TRAILING); /* leading zero */
        mm_jr_init(&r, "1.", 2); CHECK(mm_jr_double(&r, &d) != 0 && r.error == MM_JR_SYNTAX);
        mm_jr_init(&r, ".5", 2); CHECK(mm_jr_double(&r, &d) != 0 && r.error == MM_JR_SYNTAX);
        mm_jr_init(&r, "+1", 2); CHECK(mm_jr_double(&r, &d) != 0 && r.error == MM_JR_SYNTAX);
        mm_jr_init(&r, "1e999", 5); CHECK(mm_jr_double(&r, &d) != 0 && r.error == MM_JR_RANGE);   /* not finite */
        mm_jr_init(&r, "NaN", 3); CHECK(mm_jr_double(&r, &d) != 0 && r.error == MM_JR_SYNTAX);
        mm_jr_init(&r, "\"1\"", 3); CHECK(mm_jr_double(&r, &d) != 0 && r.error == MM_JR_TYPE);
    }

    /* Literals. */
    {
        int b;
        mm_jr_init(&r, "true", 4); CHECK(mm_jr_bool(&r, &b) == 0 && b == 1);
        mm_jr_init(&r, "false", 5); CHECK(mm_jr_bool(&r, &b) == 0 && b == 0);
        mm_jr_init(&r, "null", 4); CHECK(mm_jr_null(&r) == 0);
        mm_jr_init(&r, "tru", 3); CHECK(mm_jr_bool(&r, &b) != 0 && r.error == MM_JR_TRUNCATED);
        mm_jr_init(&r, "nul!", 4); CHECK(mm_jr_null(&r) != 0 && r.error == MM_JR_SYNTAX);
        mm_jr_init(&r, "null", 4); CHECK(mm_jr_bool(&r, &b) != 0 && r.error == MM_JR_TYPE);
    }

    /* Objects and arrays, walked the way the decoders walk them. */
    {
        static const char doc[] = " { \"a\" : 1 , \"b\" : [ true , null , \"x\" , { } , [ ] ] , \"c\" : { \"d\" : -2.5 } } ";
        char key[8];
        long long i; double d; int b, more;
        mm_jr_init(&r, doc, sizeof doc - 1);
        CHECK(mm_jr_object_begin(&r) == 0);
        CHECK(mm_jr_object_next(&r, key, sizeof key) == 1); CHECK_STR_EQ("a", key); CHECK(mm_jr_int(&r, &i) == 0 && i == 1);
        CHECK(mm_jr_object_next(&r, key, sizeof key) == 1); CHECK_STR_EQ("b", key);
        CHECK(mm_jr_array_begin(&r) == 0);
        CHECK(mm_jr_array_next(&r) == 1); CHECK(mm_jr_bool(&r, &b) == 0 && b);
        CHECK(mm_jr_array_next(&r) == 1); CHECK(mm_jr_peek(&r) == 'n'); CHECK(mm_jr_null(&r) == 0);
        CHECK(mm_jr_array_next(&r) == 1); CHECK(mm_jr_string(&r, key, sizeof key) == 0); CHECK_STR_EQ("x", key);
        CHECK(mm_jr_array_next(&r) == 1); CHECK(mm_jr_skip(&r) == 0);
        CHECK(mm_jr_array_next(&r) == 1); CHECK(mm_jr_array_begin(&r) == 0); CHECK(mm_jr_array_next(&r) == 0);
        CHECK(mm_jr_array_next(&r) == 0);
        more = mm_jr_object_next(&r, key, sizeof key); CHECK(more == 1); CHECK_STR_EQ("c", key);
        CHECK(mm_jr_object_begin(&r) == 0);
        CHECK(mm_jr_object_next(&r, key, sizeof key) == 1); CHECK_STR_EQ("d", key); CHECK(mm_jr_double(&r, &d) == 0 && d == -2.5);
        CHECK(mm_jr_object_next(&r, key, sizeof key) == 0);
        CHECK(mm_jr_object_next(&r, key, sizeof key) == 0);
        CHECK(mm_jr_end(&r) == 0);
        CHECK(r.error == MM_JR_OK && r.depth == 0);
    }

    /* Raw slices: the envelope body is handed on as its exact source bytes. */
    {
        static const char doc[] = "{\"body\":{\"state\":\"chartered\",\"n\":[1,2]},\"after\":1}";
        char key[8]; const char *start; size_t n; long long i;
        mm_jr_init(&r, doc, sizeof doc - 1);
        CHECK(mm_jr_object_begin(&r) == 0);
        CHECK(mm_jr_object_next(&r, key, sizeof key) == 1);
        CHECK(mm_jr_raw(&r, &start, &n) == 0);
        CHECK(n == 31 && strncmp(start, "{\"state\":\"chartered\",\"n\":[1,2]}", n) == 0);
        CHECK(mm_jr_object_next(&r, key, sizeof key) == 1 && mm_jr_int(&r, &i) == 0 && i == 1);
        CHECK(mm_jr_object_next(&r, key, sizeof key) == 0 && mm_jr_end(&r) == 0);
    }

    /* Skipping validates what it skips. */
    CHECK(skip_doc("{\"a\":[1,{\"b\":null}],\"c\":\"\\u00E9\"}") == MM_JR_OK);
    CHECK(skip_doc("[1,2,]") == MM_JR_SYNTAX);                 /* trailing comma */
    CHECK(skip_doc("{\"a\":1,}") == MM_JR_SYNTAX);
    CHECK(skip_doc("{\"a\" 1}") == MM_JR_SYNTAX);               /* missing colon */
    CHECK(skip_doc("{a:1}") == MM_JR_SYNTAX);                   /* unquoted key */
    CHECK(skip_doc("[1 2]") == MM_JR_SYNTAX);
    CHECK(skip_doc("[1,2") == MM_JR_TRUNCATED);
    CHECK(skip_doc("{\"a\":") == MM_JR_TRUNCATED);
    CHECK(skip_doc("") == MM_JR_TRUNCATED);
    CHECK(skip_doc("{} x") == MM_JR_TRAILING);
    CHECK(skip_doc("{}{}") == MM_JR_TRAILING);
    CHECK(skip_doc("[\"a\\qb\"]") == MM_JR_SYNTAX);
    CHECK(skip_doc("[\"\xff\"]") == MM_JR_BAD_UTF8);

    /* Depth is bounded: 16 levels pass, 17 do not — a nesting bomb is refused, not recursed into. */
    CHECK(skip_doc("[[[[[[[[[[[[[[[[1]]]]]]]]]]]]]]]]") == MM_JR_OK);
    CHECK(skip_doc("[[[[[[[[[[[[[[[[[1]]]]]]]]]]]]]]]]]") == MM_JR_DEPTH);

    /* Type mismatches name the type, not the syntax. */
    {
        char key[8];
        mm_jr_init(&r, "[1]", 3); CHECK(mm_jr_object_begin(&r) != 0 && r.error == MM_JR_TYPE);
        mm_jr_init(&r, "{}", 2); CHECK(mm_jr_array_begin(&r) != 0 && r.error == MM_JR_TYPE);
        mm_jr_init(&r, "{\"k\":1}", 7); CHECK(mm_jr_object_begin(&r) == 0 && mm_jr_object_next(&r, key, sizeof key) == 1 && mm_jr_string(&r, key, sizeof key) != 0 && r.error == MM_JR_TYPE);
    }

    /* Error names, for audit lines. */
    CHECK_STR_EQ("bad_utf8", mm_jr_error_name(MM_JR_BAD_UTF8));
    CHECK_STR_EQ("too_many", mm_jr_error_name(MM_JR_TOO_MANY));
    CHECK_STR_EQ("unknown", mm_jr_error_name(99));
}
