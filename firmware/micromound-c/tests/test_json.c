#include "mm_test.h"
#include "mm_json.h"

static const char *escape(const char *s, size_t n, char *out, size_t cap)
{
    int err;
    size_t len = mm_json_escape(s, n, out, cap, &err);
    if (len == 0 && err != MM_JSON_OK) return NULL;
    return out;
}

#define ESC(literal) escape((literal), sizeof(literal) - 1, out, sizeof out)

void test_json(void)
{
    char out[256];
    char buf[512];
    mm_json w;

    /* The rule, case by case (PROTOCOL.md §2). */
    CHECK_STR_EQ("\"\"", ESC(""));
    CHECK_STR_EQ("\"plain\"", ESC("plain"));
    CHECK_STR_EQ("\"a\\\"b\"", ESC("a\"b"));
    CHECK_STR_EQ("\"back\\\\slash\"", ESC("back\\slash"));
    CHECK_STR_EQ("\"+<>&'/\"", ESC("+<>&'/"));                         /* literal, unlike STJ's default encoder */
    CHECK_STR_EQ("\"\\b\\t\\n\\f\\r\"", ESC("\b\t\n\f\r"));
    CHECK_STR_EQ("\"\\u0000\\u001B\\u000B\"", ESC("\0\x1b\x0b"));      /* other controls: \u, UPPERCASE hex */
    CHECK_STR_EQ("\" ~\"", ESC(" ~"));                                  /* the ends of the literal range */
    CHECK_STR_EQ("\"\\u007F\"", ESC("\x7f"));                          /* DEL is escaped */
    CHECK_STR_EQ("\"\\u00E9\"", ESC("\xc3\xa9"));                      /* é */
    CHECK_STR_EQ("\"\\u6F22\\u5B57\"", ESC("\xe6\xbc\xa2\xe5\xad\x97")); /* 漢字 */
    CHECK_STR_EQ("\"\\u2028\"", ESC("\xe2\x80\xa8"));
    CHECK_STR_EQ("\"\\uD83D\\uDE00\"", ESC("\xf0\x9f\x98\x80"));       /* 😀 as a surrogate pair */
    CHECK_STR_EQ("\"\\uDBFF\\uDFFF\"", ESC("\xf4\x8f\xbf\xbf"));       /* U+10FFFF */
    CHECK_STR_EQ("\"\\uD800\\uDC00\"", ESC("\xf0\x90\x80\x80"));       /* U+10000 */

    /* Invalid UTF-8 is refused, never guessed at. */
    CHECK(ESC("\xc3") == NULL);                 /* truncated */
    CHECK(ESC("\xed\xa0\x80") == NULL);         /* encoded surrogate */
    CHECK(ESC("\xc0\x80") == NULL);             /* overlong NUL */
    CHECK(ESC("\xf4\x90\x80\x80") == NULL);     /* above U+10FFFF */
    CHECK(ESC("\x80") == NULL);                 /* stray continuation */

    /* Overflow reports the needed length and fails rather than truncating silently. */
    {
        int err;
        size_t n = mm_json_escape("hello", 5, out, 4, &err);
        CHECK(n == 0 && err == MM_JSON_OVERFLOW);
    }

    /* Structure: commas, nesting, every value type, empty containers. */
    mm_json_init(&w, buf, sizeof buf);
    mm_json_object_begin(&w);
    mm_json_kv_string(&w, "s", "x");
    mm_json_kv_int(&w, "i", -42);
    mm_json_kv_double(&w, "d", 0.1);
    mm_json_kv_bool(&w, "t", 1);
    mm_json_kv_bool(&w, "f", 0);
    mm_json_kv_null(&w, "n");
    mm_json_key(&w, "a");
    mm_json_array_begin(&w);
    mm_json_int(&w, 1);
    mm_json_string(&w, "two");
    mm_json_array_begin(&w);
    mm_json_array_end(&w);
    mm_json_object_begin(&w);
    mm_json_object_end(&w);
    mm_json_array_end(&w);
    mm_json_key(&w, "o");
    mm_json_object_begin(&w);
    mm_json_key(&w, "k\xc3\xa9y\"q");
    mm_json_string(&w, "v");
    mm_json_object_end(&w);
    mm_json_key(&w, "raw");
    mm_json_raw(&w, "{\"pre\":1}", 9);
    mm_json_object_end(&w);
    CHECK(mm_json_finish(&w) > 0);
    CHECK_STR_EQ("{\"s\":\"x\",\"i\":-42,\"d\":0.1,\"t\":true,\"f\":false,\"n\":null,\"a\":[1,\"two\",[],{}],\"o\":{\"k\\u00E9y\\\"q\":\"v\"},\"raw\":{\"pre\":1}}", buf);

    /* A top-level array and a top-level scalar. */
    mm_json_init(&w, buf, sizeof buf);
    mm_json_array_begin(&w);
    mm_json_double(&w, 1e21);
    mm_json_double(&w, -0.0);
    mm_json_array_end(&w);
    CHECK(mm_json_finish(&w) > 0);
    CHECK_STR_EQ("[1E+21,-0]", buf);

    mm_json_init(&w, buf, sizeof buf);
    mm_json_string(&w, "alone");
    CHECK(mm_json_finish(&w) == 7);
    CHECK_STR_EQ("\"alone\"", buf);

    /* Misuse is an error, not a corrupt document. */
    mm_json_init(&w, buf, sizeof buf);
    mm_json_object_begin(&w);
    CHECK(mm_json_finish(&w) == 0);             /* unterminated */

    mm_json_init(&w, buf, sizeof buf);
    mm_json_array_begin(&w);
    mm_json_object_end(&w);                     /* mismatched */
    CHECK(!mm_json_ok(&w) && w.error == MM_JSON_DEPTH);

    mm_json_init(&w, buf, sizeof buf);
    mm_json_key(&w, "k");                       /* a key outside any object */
    CHECK(w.error == MM_JSON_DEPTH);

    mm_json_init(&w, buf, sizeof buf);
    mm_json_object_begin(&w);
    mm_json_kv_double(&w, "nan", 0.0 / 0.0);
    mm_json_object_end(&w);
    CHECK(mm_json_finish(&w) == 0 && w.error == MM_JSON_NONFINITE);

    /* Overflow: the document fails and len says how much it needed. */
    mm_json_init(&w, buf, 10);
    mm_json_object_begin(&w);
    mm_json_kv_string(&w, "key", "a longer value");
    mm_json_object_end(&w);
    CHECK(mm_json_finish(&w) == 0 && w.error == MM_JSON_OVERFLOW && w.len == 24);

    /* Exactly enough room for the bytes but not the NUL is still a success (the NUL is optional). */
    mm_json_init(&w, buf, 2);
    mm_json_object_begin(&w);
    mm_json_object_end(&w);
    CHECK(mm_json_finish(&w) == 2 && buf[0] == '{' && buf[1] == '}');
}
