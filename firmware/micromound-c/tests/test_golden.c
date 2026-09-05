/*
 * The point of the whole library: the same inputs the C# tests freeze in
 * tests/Micromound.Tests/Golden/files must come out of this code byte for byte. Each file is
 * parsed here in the simplest way that works; the inputs are re-declared in C (nothing is read
 * back from the fixture except the expected output and, for the chain, each prev_digest).
 */
#include "mm_test.h"
#include "mm_bodies.h"
#include "mm_envelope.h"
#include "mm_format.h"
#include "mm_json.h"
#include "mm_sha256.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define LINE_MAX_LEN 8192

static FILE *open_golden(const char *name)
{
    char path[1024];
    FILE *f;
    snprintf(path, sizeof path, "%s/%s", mm_test_golden_dir, name);
    f = fopen(path, "rb");
    if (!f) printf("  cannot open %s\n", path);
    return f;
}

/* Reads one line without its newline; returns 0 at EOF. */
static int read_line(FILE *f, char *line, size_t cap)
{
    size_t n;
    if (!fgets(line, (int)cap, f)) return 0;
    n = strlen(line);
    while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r')) line[--n] = '\0';
    return 1;
}

/* Bounded copy; a truncated fixture line would fail its comparison rather than overflow. */
static void copy_str(char *dst, size_t cap, const char *src)
{
    size_t n = strlen(src);
    if (n >= cap) n = cap - 1;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

static const char *after_prefix(const char *line, const char *prefix)
{
    size_t n = strlen(prefix);
    if (strncmp(line, prefix, n) != 0) return NULL;
    line += n;
    while (*line == ' ') line++;
    return line;
}

/* ---- the golden inputs, as CanonicalBytesTests.cs declares them ---------------------------- */

static const char MOUND_ID[] = "mm-7f3a0000-0000-4000-8000-000000000001";
static const char CHARTER_ID[] = "c0000000-0000-4000-8000-000000000001";

static void golden_sync_body(mm_json *w, const void *ctx)
{
    (void)ctx;
    mm_json_object_begin(w);
    mm_json_kv_string(w, "state", "chartered");
    mm_json_kv_int(w, "uptime_s", 3600);
    mm_json_object_end(w);
}

static const char *const ACTION_EVIDENCE_REFS[] = { "e0000000-0000-4000-8000-000000000001" };
static const mm_param ACTION_PARAMS[] = { { "on_s", 30 } };

static mm_action_record golden_action_record(void)
{
    mm_action_record r;
    memset(&r, 0, sizeof r);
    r.action_id = "a0000000-0000-4000-8000-000000000001";
    r.mission_id = "";
    r.charter_id = CHARTER_ID;
    r.capability = "act.relay_1";
    r.routine_id = "";
    r.requested_parameters = NULL;
    r.n_requested_parameters = 0;
    r.parameters = ACTION_PARAMS;
    r.n_parameters = 1;
    r.started_at = "2026-08-14T21:04:11Z";
    r.ended_at = "2026-08-14T21:04:41Z";
    r.outcome = "succeeded";
    r.evidence_required = 0;
    r.evidence_refs = ACTION_EVIDENCE_REFS;
    r.n_evidence_refs = 1;
    r.detail = "";
    return r;
}

static const char *const CHARTER_CAPABILITIES[] = { "sense.temp", "act.relay_1" };
static const char *const CHARTER_REQUIRED_FOR[] = { "act.*" };

static mm_charter golden_charter(mm_limit_entry limits[1])
{
    mm_charter c;
    memset(&c, 0, sizeof c);
    memset(limits, 0, sizeof limits[0]);
    limits[0].capability = "act.relay_1";
    limits[0].limits.max_on_s.present = 1;
    limits[0].limits.max_on_s.value = 30;
    limits[0].limits.min_off_s.present = 1;
    limits[0].limits.min_off_s.value = 300;

    c.charter_id = CHARTER_ID;
    c.mound_id = MOUND_ID;
    c.mission_ref = "mission-0001";
    c.issued_at = "2026-08-14T21:04:11Z";
    c.expires_at = "2026-08-14T22:04:11Z";
    c.lease_ttl_s = 900;
    c.action_ceiling = "benign";
    c.capabilities = CHARTER_CAPABILITIES;
    c.n_capabilities = 2;
    c.routines = NULL;
    c.n_routines = 0;
    c.limits = limits;
    c.n_limits = 1;
    c.evidence_required_for = CHARTER_REQUIRED_FOR;
    c.n_evidence_required_for = 1;
    c.evidence_min_interval_s = 60;
    c.safe_state = "all_actuators_off";
    c.sync_interval_s = 15;
    return c;
}

/* ---- canonical-envelopes.txt ------------------------------------------------------------- */

static void check_envelopes(void)
{
    FILE *f = open_golden("canonical-envelopes.txt");
    char line[LINE_MAX_LEN];
    char prev_digest[LINE_MAX_LEN] = "", canonical[LINE_MAX_LEN] = "", digest[LINE_MAX_LEN] = "";
    char kind[64] = "";
    char last_digest[MM_DIGEST_TEXT_LEN + 1] = "";
    long seq = -1;
    int blocks = 0, rebuilt = 0;
    static const char *const ids[] = {
        "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222",
        "33333333-3333-4333-8333-333333333333", "44444444-4444-4444-8444-444444444444",
        "55555555-5555-4555-8555-555555555555", "66666666-6666-4666-8666-666666666666" };
    static const char *const sent_at[] = {
        "2026-08-14T21:04:11Z", "2026-08-14T21:04:12Z", "2026-08-14T21:04:13Z",
        "2026-08-14T21:04:14Z", "2026-08-14T21:04:15Z", "2026-08-14T21:04:16Z" };

    CHECK(f != NULL);
    if (!f) return;

    while (read_line(f, line, sizeof line)) {
        const char *v;
        if ((v = after_prefix(line, "## seq ")) != NULL) {
            const char *sp = strrchr(v, ' ');
            seq = strtol(v, NULL, 10);
            copy_str(kind, sizeof kind, sp ? sp + 1 : "");
            continue;
        }
        if ((v = after_prefix(line, "prev_digest:")) != NULL) {
            copy_str(prev_digest, sizeof prev_digest, strcmp(v, "(chain anchor)") == 0 ? "" : v);
            continue;
        }
        if ((v = after_prefix(line, "canonical:")) != NULL) { copy_str(canonical, sizeof canonical, v); continue; }
        if ((v = after_prefix(line, "digest:")) != NULL) {
            char computed[MM_DIGEST_TEXT_LEN + 1];
            char built[LINE_MAX_LEN];
            size_t n;
            mm_envelope e;

            copy_str(digest, sizeof digest, v);
            blocks++;

            /* Every envelope: the digest is sha256 over the canonical text, and the chain links. */
            mm_envelope_digest(canonical, strlen(canonical), computed);
            CHECK_STR_EQ(digest, computed);
            CHECK_STR_EQ(last_digest, prev_digest);
            copy_str(last_digest, sizeof last_digest, digest);

            /* The reduced-profile kinds: rebuilt from their inputs, byte for byte. */
            CHECK(seq >= 0 && seq < 6);
            if (seq < 0 || seq >= 6) continue;
            memset(&e, 0, sizeof e);
            e.id = ids[seq];
            e.mound_id = MOUND_ID;
            e.seq = seq;
            e.sent_at = sent_at[seq];
            e.kind = kind;
            e.prev_digest = prev_digest;
            if (strcmp(kind, "mound_sync") == 0) {
                e.body = golden_sync_body;
            } else if (strcmp(kind, "action_record") == 0) {
                static mm_action_record r;
                r = golden_action_record();
                e.body = mm_body_action_record;
                e.body_ctx = &r;
            } else if (strcmp(kind, "charter") == 0) {
                static mm_charter c;
                static mm_limit_entry limits[1];
                c = golden_charter(limits);
                e.body = mm_body_charter;
                e.body_ctx = &c;
            } else {
                continue; /* mission, mission_report, evidence_bundle: outside the reduced profile (§8) */
            }
            n = mm_envelope_canonical(&e, built, sizeof built, NULL);
            CHECK(n > 0);
            CHECK_STR_EQ(canonical, built);
            mm_envelope_digest(built, n, computed);
            CHECK_STR_EQ(digest, computed);
            rebuilt++;
        }
    }
    fclose(f);
    CHECK(blocks == 6);
    CHECK(rebuilt == 3);
}

/* ---- canonical-bodies.txt ---------------------------------------------------------------- */

static void check_bodies(void)
{
    FILE *f = open_golden("canonical-bodies.txt");
    char line[LINE_MAX_LEN], label[64] = "";
    int checked = 0;

    CHECK(f != NULL);
    if (!f) return;

    while (read_line(f, line, sizeof line)) {
        const char *v;
        if ((v = after_prefix(line, "## ")) != NULL) { copy_str(label, sizeof label, v); continue; }
        if (line[0] != '{') continue;

        if (strcmp(label, "charter") == 0 || strcmp(label, "action_record") == 0) {
            char built[LINE_MAX_LEN];
            mm_json w;
            mm_limit_entry limits[1];
            mm_charter c;
            mm_action_record r;
            mm_json_init(&w, built, sizeof built);
            if (label[0] == 'c') { c = golden_charter(limits); mm_body_charter(&w, &c); }
            else { r = golden_action_record(); mm_body_action_record(&w, &r); }
            CHECK(mm_json_finish(&w) > 0);
            CHECK_STR_EQ(line, built);
            checked++;
        }
    }
    fclose(f);
    CHECK(checked == 2);
}

/* ---- canonical-strings.txt --------------------------------------------------------------- */

static void check_strings(void)
{
    FILE *f = open_golden("canonical-strings.txt");
    char line[LINE_MAX_LEN];
    int vectors = 0, saw_property_case = 0;

    CHECK(f != NULL);
    if (!f) return;

    while (read_line(f, line, sizeof line)) {
        char *tab = strchr(line, '\t');
        if (line[0] == '#' || line[0] == '\0') continue;
        if (line[0] == '{') {
            /* "## as a property name and value": {"kéy\"q":"v\\al "} */
            char built[256];
            mm_json w;
            mm_json_init(&w, built, sizeof built);
            mm_json_object_begin(&w);
            mm_json_key(&w, "k\xc3\xa9y\"q");
            mm_json_string(&w, "v\\al\xe2\x80\xa8");
            mm_json_object_end(&w);
            CHECK(mm_json_finish(&w) > 0);
            CHECK_STR_EQ(line, built);
            saw_property_case = 1;
            continue;
        }
        if (!tab) continue;
        {
            size_t hexlen = (size_t)(tab - line), n = hexlen / 2;
            uint8_t bytes[LINE_MAX_LEN / 2];
            char built[LINE_MAX_LEN];
            int err;
            CHECK(hexlen % 2 == 0);
            CHECK(mm_hex_parse(line, n, bytes) == 0);
            CHECK(mm_json_escape((const char *)bytes, n, built, sizeof built, &err) > 0);
            CHECK_STR_EQ(tab + 1, built);
            vectors++;
        }
    }
    fclose(f);
    CHECK(vectors >= 20);
    CHECK(saw_property_case);
}

/* ---- canonical-doubles.txt --------------------------------------------------------------- */

static void check_doubles(void)
{
    FILE *f = open_golden("canonical-doubles.txt");
    char line[LINE_MAX_LEN];
    int vectors = 0, saw_body_case = 0;

    CHECK(f != NULL);
    if (!f) return;

    while (read_line(f, line, sizeof line)) {
        char *tab = strchr(line, '\t');
        if (line[0] == '#' || line[0] == '\0') continue;
        if (line[0] == '{') {
            /* "## in a body" */
            char built[256];
            mm_json w;
            mm_json_init(&w, built, sizeof built);
            mm_json_object_begin(&w);
            mm_json_kv_double(&w, "on_s", 30);
            mm_json_kv_double(&w, "temp_c", 28.4);
            mm_json_kv_double(&w, "ratio", 1.0 / 3);
            mm_json_kv_double(&w, "tiny", 1e-7);
            mm_json_kv_double(&w, "huge", 1e21);
            mm_json_kv_double(&w, "neg_zero", -0.0);
            mm_json_object_end(&w);
            CHECK(mm_json_finish(&w) > 0);
            CHECK_STR_EQ(line, built);
            saw_body_case = 1;
            continue;
        }
        if (!tab || tab - line != 16) continue;
        {
            uint8_t bits[8];
            uint64_t u = 0;
            double value;
            char built[MM_FORMAT_DOUBLE_MAX];
            int i;
            CHECK(mm_hex_parse(line, 8, bits) == 0);
            for (i = 0; i < 8; i++) u = (u << 8) | bits[i];
            memcpy(&value, &u, sizeof value);          /* IEEE-754 binary64, host byte order assumed by memcpy */
            if (mm_format_double(value, built, sizeof built) == 0) {
                printf("  FAIL: no text for %s\n", line);
                mm_test_failures++;
                continue;
            }
            mm_test_checks++;
            if (strcmp(tab + 1, built) != 0) {
                mm_test_failures++;
                printf("  FAIL %s: expected %s, got %s\n", line, tab + 1, built);
            }
            vectors++;
        }
    }
    fclose(f);
    CHECK(vectors >= 250);
    CHECK(saw_body_case);
}

void test_golden(void)
{
    check_envelopes();
    check_bodies();
    check_strings();
    check_doubles();
}
