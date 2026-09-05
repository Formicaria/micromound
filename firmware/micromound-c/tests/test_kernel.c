/*
 * The kernel against kernel-decisions.txt: the same device, the same clock, the same script, every
 * line reproduced. The fixture is the source of truth for the EXPECTATIONS; the script's inputs
 * are read from its `at:` and `request:` lines, and the few events and executor moods the C# test
 * scripts by hand are keyed off the step labels below.
 */
#include "mm_test.h"
#include "mm_bodies.h"
#include "mm_decode.h"
#include "mm_json.h"
#include "mm_json_read.h"
#include "mm_kernel.h"
#include "mm_time.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define LINE_MAX_LEN 4096

static const char MOUND[] = "mm-7f3a0000-0000-4000-8000-000000000001";

/* ---- the device, as KernelDecisionsTests.Device() declares it ---------------------------- */

static const char *const RELAY_PARAMS[] = { "on_s" };
static const mm_param_range RELAY_RANGES[] = { { "on_s", 1, 3600 } };
static const char *const DIMMER_PARAMS[] = { "level" };
static const mm_param_range DIMMER_RANGES[] = { { "level", 0, 100 } };
static const char *const COOL_DRIVES[] = { "act.relay_1" };

#define SET(v) { 1, (v) }
#define UNSET { 0, 0 }

static const mm_capability_desc CAPS[] = {
    { "sense.temp", 0, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL },
    { "act.relay_1", 1, { SET(60), SET(120), UNSET, UNSET, SET(4) }, RELAY_PARAMS, 1, RELAY_PARAMS, 1, RELAY_RANGES, 1, "on_s", NULL },
    { "act.dimmer", 2, { UNSET, UNSET, UNSET, SET(80), UNSET }, DIMMER_PARAMS, 1, NULL, 0, DIMMER_RANGES, 1, NULL, "level" },
    { "act.fan", 1, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL },
};

static const mm_routine_desc ROUTINES[] = {
    { "routine.cool", 1, COOL_DRIVES, 1, { SET(45), UNSET, UNSET, UNSET, UNSET }, RELAY_PARAMS, 1, NULL, 0, NULL, 0, "on_s", NULL },
};

/* ScriptedExecutor: succeeds, ends at start + on_s, produces one reading taken at the start. */
typedef struct scripted {
    const char *id;
    int produce_evidence;
    int stale_by;
    int fault;
    int n;
} scripted;

static int scripted_run(void *ctx, const mm_execution *x, mm_outcome *out)
{
    scripted *s = (scripted *)ctx;
    double duration = 0;
    size_t i;
    if (s->fault) { out->succeeded = 0; strcpy(out->detail, "relay did not answer"); return 0; }
    for (i = 0; i < x->n_parameters; i++) if (strcmp(x->parameters[i].key, "on_s") == 0) duration = x->parameters[i].value;
    out->succeeded = 1;
    out->has_ended_at = 1;
    out->ended_at = x->started_at + (int64_t)duration;
    if (!s->produce_evidence) return 0;
    s->n++;
    snprintf(out->evidence[0].id, MM_ID_CAP, "e-%s-%d", s->id, s->n);
    mm_time_format(x->started_at - s->stale_by, out->evidence[0].captured_at, MM_TIME_TEXT_CAP);
    out->n_evidence = 1;
    return 0;
}

/* ---- the charters the script offers ---------------------------------------------------- */

static void base_charter(mm_charter_in *c, const char *id, const char *ceiling, long long lease)
{
    memset(c, 0, sizeof *c);
    strcpy(c->charter_id, id);
    strcpy(c->mound_id, MOUND);
    strcpy(c->mission_ref, "mission-0001");
    strcpy(c->issued_at, "2026-08-14T21:04:11Z");
    strcpy(c->expires_at, "2026-08-15T00:04:11Z");
    c->lease_ttl_s = lease;
    strcpy(c->action_ceiling, ceiling);
    c->evidence_min_interval_s = 60;
    strcpy(c->safe_state, "all_actuators_off");
    c->sync_interval_s = 15;
}

static void benign_charter(mm_charter_in *c)
{
    base_charter(c, "c0000000-0000-4000-8000-000000000001", "benign", 3600);
    strcpy(c->capabilities[0], "sense.temp"); strcpy(c->capabilities[1], "act.relay_1"); strcpy(c->capabilities[2], "act.fan");
    c->n_capabilities = 3;
    strcpy(c->routines[0], "routine.cool"); c->n_routines = 1;
    strcpy(c->limits[0].capability, "act.relay_1");
    c->limits[0].limits.max_on_s.present = 1; c->limits[0].limits.max_on_s.value = 30;
    c->limits[0].limits.min_off_s.present = 1; c->limits[0].limits.min_off_s.value = 300;
    c->limits[0].limits.max_rate_per_h.present = 1; c->limits[0].limits.max_rate_per_h.value = 10;
    c->n_limits = 1;
    strcpy(c->evidence_required_for[0], "act.*"); c->n_evidence_required_for = 1;
}

static void observe_charter(mm_charter_in *c)
{
    base_charter(c, "c0000000-0000-4000-8000-000000000002", "observe", 600);
    strcpy(c->capabilities[0], "sense.temp"); c->n_capabilities = 1;
}

static void expired_charter(mm_charter_in *c)
{
    base_charter(c, "c0000000-0000-4000-8000-000000000003", "benign", 600);
    strcpy(c->capabilities[0], "act.relay_1"); c->n_capabilities = 1;
    strcpy(c->expires_at, "2026-08-14T21:04:11Z");
}

static void mismatched_charter(mm_charter_in *c)
{
    base_charter(c, "c0000000-0000-4000-8000-000000000004", "benign", 600);
    strcpy(c->capabilities[0], "act.relay_1"); strcpy(c->capabilities[1], "act.laser"); c->n_capabilities = 2;
    strcpy(c->limits[0].capability, "act.dimmer");
    c->limits[0].limits.max.present = 1; c->limits[0].limits.max.value = 10;
    c->n_limits = 1;
}

/* The C# Accept(): "charter accepted" | "charter refused: <reasons>" [+ " | review: <notes>"]. */
static void accept(mm_kernel *k, const mm_charter_in *c, int64_t now, char *out, size_t cap)
{
    mm_refusal why;
    char reasons[1024], review[512];
    int n = mm_kernel_accept_charter(k, c, now, &why);
    int notes = mm_kernel_review_charter(k, c, review, sizeof review);
    if (n == 0) snprintf(out, cap, "charter accepted%s%s", notes ? " | review: " : "", notes ? review : "");
    else snprintf(out, cap, "charter refused: %s%s%s", mm_refusal_join(&why, reasons, sizeof reasons), notes ? " | review: " : "", notes ? review : "");
}

/* ---- helpers ------------------------------------------------------------------------------ */

static int read_line(FILE *f, char *line, size_t cap)
{
    size_t n;
    if (!fgets(line, (int)cap, f)) return 0;
    n = strlen(line);
    while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r')) line[--n] = '\0';
    return 1;
}

static const char *after_prefix(const char *line, const char *prefix)
{
    size_t n = strlen(prefix);
    if (strncmp(line, prefix, n) != 0) return NULL;
    line += n;
    while (*line == ' ') line++;
    return line;
}

static void copy_str(char *dst, size_t cap, const char *src)
{
    size_t n = strlen(src);
    if (n >= cap) n = cap - 1;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

/* Parses `<capability> {json params}[ worker_ceiling=<class>]` into a request over the given storage. */
static int parse_request(const char *text, char *cap_buf, char keys[][MM_NAME_CAP], mm_param *params, mm_request *r)
{
    const char *sp = strchr(text, ' ');
    const char *json, *tail;
    mm_jr jr;
    int more;
    size_t n = 0;

    if (!sp) return -1;
    memcpy(cap_buf, text, (size_t)(sp - text));
    cap_buf[sp - text] = '\0';
    json = sp + 1;
    tail = strstr(json, " worker_ceiling=");

    mm_jr_init(&jr, json, tail ? (size_t)(tail - json) : strlen(json));
    if (mm_jr_object_begin(&jr) != 0) return -1;
    while ((more = mm_jr_object_next(&jr, keys[n], MM_NAME_CAP)) == 1) {
        if (mm_jr_double(&jr, &params[n].value) != 0) return -1;
        params[n].key = keys[n];
        n++;
    }
    if (more != 0 || mm_jr_end(&jr) != 0) return -1;

    memset(r, 0, sizeof *r);
    r->capability = cap_buf;
    r->parameters = params;
    r->n_parameters = n;
    r->mission_id = "";
    r->worker = tail ? "scout" : "";
    r->worker_ceiling = tail ? mm_action_class_parse(tail + 16) : -1;
    return 0;
}

void test_kernel(void)
{
    char path[1024], line[LINE_MAX_LEN];
    FILE *f;
    mm_kernel k;
    char error[256];
    scripted moods[4] = { { "sense.temp", 1, 0, 0, 0 }, { "act.relay_1", 1, 0, 0, 0 }, { "act.dimmer", 1, 0, 0, 0 }, { "routine.cool", 1, 0, 0, 0 } };
    mm_executor executors[4];
    mm_device_limit device_limits[1];
    size_t i;

    /* pending step state */
    char label[256] = "", request_text[LINE_MAX_LEN] = "";
    int64_t at = 0;
    int step = 0, steps = 0, in_step = 0;
    char expect_decision[256] = "", expect_detail[LINE_MAX_LEN] = "", expect_effective[LINE_MAX_LEN] = "";
    char expect_limits[LINE_MAX_LEN] = "", expect_state[64] = "", expect_event[LINE_MAX_LEN] = "", expect_record[LINE_MAX_LEN] = "";
    int have_event = 0;

    /* The device. */
    CHECK(mm_kernel_init(&k, MOUND, CAPS, 4, ROUTINES, 1, error, sizeof error) == 0);
    if (error[0]) printf("  init: %s\n", error);
    memset(device_limits, 0, sizeof device_limits);
    strcpy(device_limits[0].id, "act.relay_1");
    device_limits[0].limits.max_on_s.present = 1; device_limits[0].limits.max_on_s.value = 40;
    mm_kernel_apply_device_limits(&k, device_limits, 1, NULL);
    for (i = 0; i < 4; i++) {
        executors[i].capability_id = moods[i].id;
        executors[i].run = scripted_run;
        executors[i].ctx = &moods[i];
        executors[i].available = 1;
        CHECK(mm_kernel_bind_executor(&k, &executors[i]) == 0);
    }
    CHECK_STR_EQ("observe_only", mm_authority_state(&k.authority));

    /* Registry rules, the ones a compiled table could break. */
    {
        mm_kernel bad;
        static const mm_capability_desc sense_act[] = { { "sense.temp", 1, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL } };
        static const mm_capability_desc act_observe[] = { { "act.x", 0, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL } };
        static const mm_capability_desc hazardous[] = { { "act.x", 3, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL } };
        static const mm_capability_desc badid[] = { { "Act.X", 1, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL } };
        static const mm_capability_desc badreq[] = { { "act.x", 1, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, RELAY_PARAMS, 1, NULL, 0, NULL, NULL } };
        static const char *const drives_dimmer[] = { "act.dimmer" };
        static const mm_routine_desc lower_class[] = { { "routine.dim", 1, drives_dimmer, 1, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL } };
        static const mm_routine_desc drives_nothing[] = { { "routine.idle", 1, NULL, 0, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL } };
        CHECK(mm_kernel_init(&bad, MOUND, sense_act, 1, NULL, 0, error, sizeof error) == -1);
        CHECK_STR_EQ("'sense.temp' is in the sense namespace and must be class 'observe'", error);
        CHECK(mm_kernel_init(&bad, MOUND, act_observe, 1, NULL, 0, error, sizeof error) == -1);
        CHECK_STR_EQ("'act.x' is in the act namespace and must declare a class above 'observe'", error);
        CHECK(mm_kernel_init(&bad, MOUND, hazardous, 1, NULL, 0, error, sizeof error) == -1);
        CHECK_STR_EQ("'act.x' cannot be registered as 'hazardous': hazardous work has no authorization pipeline yet", error);
        CHECK(mm_kernel_init(&bad, MOUND, badid, 1, NULL, 0, error, sizeof error) == -1);
        CHECK_STR_EQ("capability id 'Act.X' is not well formed", error);
        CHECK(mm_kernel_init(&bad, MOUND, badreq, 1, NULL, 0, error, sizeof error) == -1);
        CHECK_STR_EQ("'act.x': required parameter 'on_s' is not an accepted parameter", error);
        CHECK(mm_kernel_init(&bad, MOUND, CAPS, 4, lower_class, 1, error, sizeof error) == -1);
        CHECK_STR_EQ("'routine.dim' is class 'benign' but drives 'act.dimmer', which is class 'controlled'", error);
        CHECK(mm_kernel_init(&bad, MOUND, CAPS, 4, drives_nothing, 1, error, sizeof error) == -1);
        CHECK_STR_EQ("'routine.idle' drives no capabilities", error);
        CHECK(mm_capability_is_well_formed("act.relay_1") && mm_capability_is_well_formed("sense.a.b_2"));
        CHECK(!mm_capability_is_well_formed("act") && !mm_capability_is_well_formed("act.") && !mm_capability_is_well_formed(".x") &&
              !mm_capability_is_well_formed("motor.x") && !mm_capability_is_well_formed("act.Relay") && !mm_capability_is_well_formed("act..x") &&
              !mm_capability_is_well_formed("act.relay-1") && !mm_capability_is_well_formed(""));
    }

    /* LimitClamp on its own. */
    {
        mm_capability_limits a = { SET(60), SET(120), UNSET, SET(80), SET(4) }, b = { SET(30), SET(300), SET(1), UNSET, SET(10) }, r;
        mm_limits_intersect(&a, &b, &r);
        CHECK(r.max_on_s.present && r.max_on_s.value == 30);
        CHECK(r.min_off_s.present && r.min_off_s.value == 300);
        CHECK(r.min.present && r.min.value == 1);
        CHECK(r.max.present && r.max.value == 80);
        CHECK(r.max_rate_per_h.present && r.max_rate_per_h.value == 4);
        CHECK(mm_limits_attempts_to_widen(&a, &b));          /* max_rate 10 > 4 */
        CHECK(mm_limits_attempts_to_widen(&b, &a));          /* and the other way: max_on_s 60 > 30, min_off_s 120 < 300 */
        CHECK(!mm_limits_attempts_to_widen(&a, &a));
        CHECK(!mm_limits_attempts_to_widen(&a, &r));         /* the intersection never widens either side */
        CHECK(!mm_limits_attempts_to_widen(&b, &r));
    }

    /* The script. A step runs when its block ends (a blank line, or EOF). */
    snprintf(path, sizeof path, "%s/kernel-decisions.txt", mm_test_golden_dir);
    f = fopen(path, "rb");
    CHECK(f != NULL);
    if (!f) { printf("  cannot open %s\n", path); return; }

    for (;;) {
        int eof = !read_line(f, line, sizeof line);
        const char *v;

        if (!eof && line[0] != '\0') {
            if ((v = after_prefix(line, "## step ")) != NULL) {
                const char *dash = strstr(v, " \xe2\x80\x94 ");     /* " — " */
                step = atoi(v);
                copy_str(label, sizeof label, dash ? dash + 5 : "");
                in_step = 1; have_event = 0; request_text[0] = '\0';
                expect_decision[0] = expect_detail[0] = expect_effective[0] = expect_limits[0] = expect_state[0] = expect_event[0] = expect_record[0] = '\0';
            }
            else if ((v = after_prefix(line, "at:")) != NULL) {
                char t[MM_TIME_TEXT_CAP];
                const char *sp = strchr(v, ' ');
                copy_str(t, sizeof t, v);
                if (sp && (size_t)(sp - v) < sizeof t) t[sp - v] = '\0';
                CHECK(mm_time_parse(t, &at) == 0);
            }
            else if ((v = after_prefix(line, "request:")) != NULL) copy_str(request_text, sizeof request_text, v);
            else if ((v = after_prefix(line, "decision:")) != NULL) copy_str(expect_decision, sizeof expect_decision, v);
            else if ((v = after_prefix(line, "detail:")) != NULL) copy_str(expect_detail, sizeof expect_detail, v);
            else if ((v = after_prefix(line, "effective:")) != NULL) copy_str(expect_effective, sizeof expect_effective, v);
            else if ((v = after_prefix(line, "limits:")) != NULL) copy_str(expect_limits, sizeof expect_limits, v);
            else if ((v = after_prefix(line, "event:")) != NULL) { copy_str(expect_event, sizeof expect_event, v); have_event = 1; }
            else if ((v = after_prefix(line, "state:")) != NULL) copy_str(expect_state, sizeof expect_state, v);
            else if ((v = after_prefix(line, "record:")) != NULL) copy_str(expect_record, sizeof expect_record, v);
            continue;
        }

        if (in_step) {
            int before = mm_test_failures;
            steps++;

            if (have_event) {
                char actual[LINE_MAX_LEN];
                mm_charter_in c;
                if (strstr(label, "accept a benign charter") || strstr(label, "a fresh charter") || strstr(label, "a charter cannot clear a stop")) {
                    benign_charter(&c); accept(&k, &c, at, actual, sizeof actual);
                } else if (strstr(label, "renew the lease")) {
                    mm_authority_renew_lease(&k.authority, at); copy_str(actual, sizeof actual, "lease renewed");
                } else if (strstr(label, "the lease runs out")) {
                    copy_str(actual, sizeof actual, mm_authority_quiesce_if_expired(&k.authority, at) ? "quiesced" : "still alive");
                } else if (strcmp(label, "stop") == 0) {
                    mm_authority_stop(&k.authority); copy_str(actual, sizeof actual, "stopped");
                } else if (strstr(label, "the stop is cleared explicitly")) {
                    mm_authority_clear_stop(&k.authority); copy_str(actual, sizeof actual, "stop cleared");
                } else if (strstr(label, "an observe-ceiling charter")) {
                    observe_charter(&c); accept(&k, &c, at, actual, sizeof actual);
                } else if (strstr(label, "an expired charter")) {
                    expired_charter(&c); accept(&k, &c, at, actual, sizeof actual);
                } else if (strstr(label, "a charter naming what this device does not have")) {
                    mismatched_charter(&c); accept(&k, &c, at, actual, sizeof actual);
                } else {
                    snprintf(actual, sizeof actual, "(no C mapping for event '%s')", label);
                }
                CHECK_STR_EQ(expect_event, actual);
                CHECK_STR_EQ(expect_state, mm_authority_state(&k.authority));
            } else {
                char cap_buf[MM_NAME_CAP], keys[MM_MAX_PARAMS][MM_NAME_CAP];
                mm_param params[MM_MAX_PARAMS];
                mm_request req;
                mm_decision d;
                mm_action_record_in record;
                mm_action_record_view view;
                char actual[LINE_MAX_LEN], action_id[16], built[LINE_MAX_LEN];
                mm_json w;
                scripted *relay = &moods[1];

                CHECK(parse_request(request_text, cap_buf, keys, params, &req) == 0);

                /* the executor moods the C# test sets by hand around specific steps */
                if (strstr(label, "driver reports itself unavailable")) executors[1].available = 0;
                if (strstr(label, "commands are not evidence")) relay->produce_evidence = 0;
                if (strstr(label, "stale evidence")) relay->stale_by = 400;
                if (strstr(label, "driver fault")) relay->fault = 1;

                mm_kernel_authorize(&k, &req, at, &d);
                snprintf(action_id, sizeof action_id, "a-%d", step);
                mm_kernel_execute(&k, &req, at, action_id, &record);

                executors[1].available = 1;
                relay->produce_evidence = 1; relay->stale_by = 0; relay->fault = 0;

                if (d.authorized) copy_str(actual, sizeof actual, "authorized");
                else snprintf(actual, sizeof actual, "refused %s", mm_refusal_reason_wire(d.refusal));
                CHECK_STR_EQ(expect_decision, actual);
                CHECK_STR_EQ(expect_detail, d.detail);

                mm_json_init(&w, built, sizeof built);
                mm_json_object_begin(&w);
                for (i = 0; i < d.n_effective; i++) mm_json_kv_double(&w, d.effective[i].key, d.effective[i].value);
                mm_json_object_end(&w);
                CHECK(mm_json_finish(&w) > 0);
                copy_str(actual, sizeof actual, built);
                strcat(actual, d.clamped ? " clamped=true" : " clamped=false");
                strcat(actual, d.evidence_required ? " evidence_required=true" : " evidence_required=false");
                CHECK_STR_EQ(expect_effective, actual);

                mm_json_init(&w, built, sizeof built);
                mm_write_capability_limits(&w, &d.effective_limits);
                CHECK(mm_json_finish(&w) > 0);
                CHECK_STR_EQ(expect_limits, built);

                CHECK_STR_EQ(expect_state, mm_authority_state(&k.authority));

                mm_action_record_bind(&record, &view);
                mm_json_init(&w, built, sizeof built);
                mm_body_action_record(&w, &view.record);
                CHECK(mm_json_finish(&w) > 0);
                CHECK_STR_EQ(expect_record, built);
            }
            if (mm_test_failures != before) printf("    ^ step %d \xe2\x80\x94 %s\n", step, label);
            in_step = 0;
        }
        if (eof) break;
    }
    fclose(f);
    CHECK(steps == 42);
}
