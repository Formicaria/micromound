/*
 * A whole session between a reduced-profile device and a scripted controller, over a fake
 * transport, with fixed seeds, fixed ids and a fixed clock. Everything the device sends and
 * everything the controller answers is written to device-session.txt; the file is a fixture in
 * both directions — this test compares the session against the committed transcript byte for
 * byte, and the C# DeviceSessionTests reads the same file and verifies every uplink envelope with
 * the host's verifier, chain validator and typed contracts.
 */
#include "mm_test.h"
#include "mm_decode.h"
#include "mm_device.h"
#include "mm_ed25519.h"
#include "mm_envelope.h"
#include "mm_sha256.h"
#include "mm_time.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const char MOUND[] = "mm-7f3a0000-0000-4000-8000-000000000001";
static const int64_t T0 = 1786741451LL;   /* 2026-08-14T21:04:11Z */

/* ---- the same device as test_kernel.c ---------------------------------------------------- */

static const char *const RELAY_PARAMS[] = { "on_s" };
static const mm_param_range RELAY_RANGES[] = { { "on_s", 1, 3600 } };
static const char *const COOL_DRIVES[] = { "act.relay_1" };
#define SET(v) { 1, (v) }
#define UNSET { 0, 0 }
static const mm_capability_desc CAPS[] = {
    { "sense.temp", 0, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL },
    { "act.relay_1", 1, { SET(60), SET(120), UNSET, UNSET, SET(4) }, RELAY_PARAMS, 1, RELAY_PARAMS, 1, RELAY_RANGES, 1, "on_s", NULL },
};
static const mm_routine_desc ROUTINES[] = {
    { "routine.cool", 1, COOL_DRIVES, 1, { SET(45), UNSET, UNSET, UNSET, UNSET }, RELAY_PARAMS, 1, NULL, 0, NULL, 0, "on_s", NULL },
};

typedef struct fake_driver { const char *id; int n; int safe_state_entered; } fake_driver;

static int fake_run(void *ctx, const mm_execution *x, mm_outcome *out)
{
    fake_driver *f = (fake_driver *)ctx;
    double duration = 0;
    size_t i;
    for (i = 0; i < x->n_parameters; i++) if (strcmp(x->parameters[i].key, "on_s") == 0) duration = x->parameters[i].value;
    out->succeeded = 1;
    out->has_ended_at = 1;
    out->ended_at = x->started_at + (int64_t)duration;
    f->n++;
    snprintf(out->evidence[0].id, MM_ID_CAP, "e-%s-%d", f->id, f->n);
    mm_time_format(x->started_at, out->evidence[0].captured_at, MM_TIME_TEXT_CAP);
    out->n_evidence = 1;
    return 0;
}

static void enter_safe_state(void *ctx) { ((fake_driver *)ctx)->safe_state_entered++; }

/* ---- deterministic ids ---- */

typedef struct id_counter { const char *prefix; int n; } id_counter;

static void next_id(void *ctx, char out[MM_ID_CAP])
{
    id_counter *c = (id_counter *)ctx;
    snprintf(out, MM_ID_CAP, "%s-0000-4000-8000-%012d", c->prefix, ++c->n);
}

/* ---- the scripted controller ------------------------------------------------------------- */

#define MAX_LINES 256
#define LINE_CAP 4096

typedef struct controller {
    uint8_t sk[64], pk[32], device_pk[32];
    id_counter ids;
    long long seq;
    char last_digest[MM_DIGEST_TEXT_LEN + 1];
    int64_t now;
    int offline;

    /* what to send with the next exchange, besides the ack */
    int send_charter, send_duplicate_charter, send_misrouted, send_mission_kind, send_action_record, send_tampered, send_stop;
    long long charter_lease_ttl;

    /* wire storage for the current downlink batch */
    char out[MM_DEVICE_BATCH][LINE_CAP];
    size_t n_out;

    /* the transcript */
    char lines[MAX_LINES][LINE_CAP];
    size_t n_lines;
    char last_charter_wire[LINE_CAP];
    long long acked_through;
} controller;

static void transcript(controller *c, const char *tag, const char *text)
{
    char *line;
    size_t tl = strlen(tag), n = strlen(text);
    if (c->n_lines >= MAX_LINES) return;
    line = c->lines[c->n_lines++];
    memcpy(line, tag, tl);
    if (n > LINE_CAP - 1 - tl) n = LINE_CAP - 1 - tl;
    memcpy(line + tl, text, n);
    line[tl + n] = '\0';
}

static void charter_body(mm_json *w, const void *ctx)
{
    const long long *lease = (const long long *)ctx;
    static const char *const caps[] = { "sense.temp", "act.relay_1" };
    static const char *const routines[] = { "routine.cool" };
    static const char *const required_for[] = { "act.*" };
    mm_limit_entry limits[1];
    mm_charter ch;
    memset(&ch, 0, sizeof ch);
    memset(limits, 0, sizeof limits);
    limits[0].capability = "act.relay_1";
    limits[0].limits.max_on_s.present = 1; limits[0].limits.max_on_s.value = 30;
    limits[0].limits.min_off_s.present = 1; limits[0].limits.min_off_s.value = 300;
    ch.charter_id = "c0000000-0000-4000-8000-000000000001";
    ch.mound_id = MOUND;
    ch.mission_ref = "mission-0001";
    ch.issued_at = "2026-08-14T21:04:11Z";
    ch.expires_at = "2026-08-15T00:04:11Z";
    ch.lease_ttl_s = *lease;
    ch.action_ceiling = "benign";
    ch.capabilities = caps; ch.n_capabilities = 2;
    ch.routines = routines; ch.n_routines = 1;
    ch.limits = limits; ch.n_limits = 1;
    ch.evidence_required_for = required_for; ch.n_evidence_required_for = 1;
    ch.evidence_min_interval_s = 60;
    ch.safe_state = "all_actuators_off";
    ch.sync_interval_s = 15;
    mm_body_charter(w, &ch);
}

static void empty_body(mm_json *w, const void *ctx) { (void)ctx; mm_json_object_begin(w); mm_json_object_end(w); }

/* Signs and queues one downlink envelope for the current exchange. */
static const char *controller_send(controller *c, const char *mound_id, const char *kind, mm_body_writer body, const void *ctx)
{
    mm_envelope e;
    char id[MM_ID_CAP], sent_at[MM_TIME_TEXT_CAP], digest[MM_DIGEST_TEXT_LEN + 1];
    size_t n;
    char *slot = c->out[c->n_out];

    next_id(&c->ids, id);
    mm_time_format(c->now, sent_at, sizeof sent_at);
    memset(&e, 0, sizeof e);
    e.id = id; e.mound_id = mound_id; e.seq = c->seq; e.sent_at = sent_at; e.kind = kind;
    e.prev_digest = c->last_digest; e.body = body; e.body_ctx = ctx;
    n = mm_envelope_write_signed(&e, c->sk, slot, LINE_CAP, digest);
    if (n == 0) return NULL;
    c->seq++;
    memcpy(c->last_digest, digest, sizeof digest);
    c->n_out++;
    return slot;
}

static void ack_body(mm_json *w, const void *ctx)
{
    const long long *through = (const long long *)ctx;
    mm_ack a;
    memset(&a, 0, sizeof a);
    a.status = "ok"; a.refers_to = ""; a.through_seq = *through; a.detail = "";
    mm_body_ack(w, &a);
}

/* The transport: the controller verifies what came up (as ANTHILL would), acknowledges it, and adds what its script says. */
static int exchange(void *ctx, const char *wire, size_t n, mm_wire *downlink, size_t *n_downlink)
{
    controller *c = (controller *)ctx;
    mm_envelope_in frame;
    int err;
    size_t i;
    char body[LINE_CAP];

    memcpy(body, wire, n); body[n] = '\0';
    transcript(c, "up:   ", body);

    if (c->offline) { transcript(c, "link: ", "unavailable"); return -1; }

    /* verify under the device key from the bytes as received; refuse anything else */
    if (mm_envelope_verify_wire(wire, n, c->device_pk, NULL) != 0) { transcript(c, "ctl:  ", "REJECTED: signature"); return 0; }
    if (mm_envelope_parse(wire, n, &frame, &err) != 0) { transcript(c, "ctl:  ", "REJECTED: unreadable"); return 0; }

    c->n_out = 0;
    if (frame.seq > c->acked_through) c->acked_through = frame.seq;
    controller_send(c, MOUND, MM_KIND_ACK, ack_body, &c->acked_through);

    if (c->send_charter) {
        const char *w = controller_send(c, MOUND, MM_KIND_CHARTER, charter_body, &c->charter_lease_ttl);
        if (w) strcpy(c->last_charter_wire, w);
        c->send_charter = 0;
    }
    if (c->send_duplicate_charter) {                                   /* the same envelope again: re-delivery */
        strcpy(c->out[c->n_out], c->last_charter_wire); c->n_out++;
        c->send_duplicate_charter = 0;
    }
    if (c->send_misrouted) { controller_send(c, "mm-someone-else", MM_KIND_STOP, empty_body, NULL); c->send_misrouted = 0; }
    if (c->send_mission_kind) { controller_send(c, MOUND, "mission", empty_body, NULL); c->send_mission_kind = 0; }
    if (c->send_action_record) { controller_send(c, MOUND, MM_KIND_ACTION_RECORD, empty_body, NULL); c->send_action_record = 0; }
    if (c->send_tampered) {                                            /* a charter with one body byte changed after signing */
        char *w = c->out[c->n_out];
        strcpy(w, c->last_charter_wire);
        { char *p = strstr(w, "\"lease_ttl_s\":"); if (p) p[14] = '9'; }
        c->n_out++;
        c->send_tampered = 0;
    }
    if (c->send_stop) {
        mm_stop st; st.reason = "operator stop";
        controller_send(c, MOUND, MM_KIND_STOP, mm_body_stop, &st);
        c->send_stop = 0;
    }

    for (i = 0; i < c->n_out; i++) {
        downlink[i].bytes = c->out[i];
        downlink[i].n = strlen(c->out[i]);
        transcript(c, "down: ", c->out[i]);
    }
    *n_downlink = c->n_out;
    return 0;
}

/* ---- the transcript file ------------------------------------------------------------------ */

static void compare_or_create(const controller *c, const char *device_pk_hex)
{
    char path[1024], line[LINE_CAP];
    FILE *f;
    size_t i = 0, matched = 0;
    int mismatch = 0;

    snprintf(path, sizeof path, "%s/device-session.txt", mm_test_golden_dir);
    f = fopen(path, "rb");
    if (!f) {
        f = fopen(path, "wb");
        if (!f) { printf("  cannot create %s\n", path); mm_test_failures++; return; }
        fprintf(f, "# MICROMOUND device session — transcript fixture\n#\n");
        fprintf(f, "# Written by firmware/micromound-c/tests/test_device.c: a reduced-profile device (mm_device) and a scripted\n");
        fprintf(f, "# controller over a fake transport, fixed seeds (device 00 01 02 …, controller 20 21 22 …), fixed ids, fixed clock.\n");
        fprintf(f, "# `up:` is what the device sent, `down:` what the controller answered, in order. The C test compares the\n");
        fprintf(f, "# session against this file; the C# DeviceSessionTests verifies every `up:` line with the host's verifier and\n");
        fprintf(f, "# chain validator and decodes every body through the typed contracts. A change here is a protocol change.\n");
        fprintf(f, "device_pk:     %s\n", device_pk_hex);
        fprintf(f, "controller_pk: ");
        for (i = 0; i < 32; i++) fprintf(f, "%02x", c->pk[i]);
        fprintf(f, "\n\n");
        for (i = 0; i < c->n_lines; i++) fprintf(f, "%s\n", c->lines[i]);
        fclose(f);
        printf("  transcript created at %s — review it, then run again; the C# DeviceSessionTests reads it too\n", path);
        mm_test_failures++;
        return;
    }

    while (fgets(line, sizeof line, f)) {
        size_t n = strlen(line);
        while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r')) line[--n] = '\0';
        if (line[0] == '#' || line[0] == '\0' || strncmp(line, "device_pk:", 10) == 0 || strncmp(line, "controller_pk:", 14) == 0) continue;
        if (matched < c->n_lines) {
            mm_test_checks++;
            if (strcmp(line, c->lines[matched]) != 0) {
                mm_test_failures++;
                if (!mismatch) printf("  FAIL transcript line %d:\n    expected: %.200s\n    actual:   %.200s\n", (int)matched + 1, line, c->lines[matched]);
                mismatch = 1;
            }
        }
        matched++;
    }
    fclose(f);
    CHECK(matched == c->n_lines);
}

/* ---- the session --------------------------------------------------------------------------- */

void test_device(void)
{
    static controller ctl;                       /* large */
    static mm_device dev;                        /* large */
    mm_device_config cfg;
    fake_driver sensor = { "sense.temp", 0, 0 }, relay = { "act.relay_1", 0, 0 }, cool = { "routine.cool", 0, 0 };
    mm_executor executors[3];
    id_counter device_ids = { "d0000000", 0 };
    uint8_t seed[32], device_sk[64], device_pk[32];
    char error[256], device_pk_hex[65];
    mm_sync_outcome out;
    mm_action_record_in record;
    mm_param on_s = { "on_s", 50 };
    mm_request act_relay = { "act.relay_1", &on_s, 1, "", "", -1 };
    mm_request sense = { "sense.temp", NULL, 0, "", "", -1 };
    size_t i;

    /* identities */
    for (i = 0; i < 32; i++) seed[i] = (uint8_t)i;
    mm_ed25519_seed_keypair(device_pk, device_sk, seed);
    mm_hex_lower(device_pk, 32, device_pk_hex);
    memset(&ctl, 0, sizeof ctl);
    for (i = 0; i < 32; i++) seed[i] = (uint8_t)(0x20 + i);
    mm_ed25519_seed_keypair(ctl.pk, ctl.sk, seed);
    memcpy(ctl.device_pk, device_pk, 32);
    ctl.ids.prefix = "c0000000";
    ctl.acked_through = -1;
    ctl.charter_lease_ttl = 3600;

    /* the device */
    memset(&cfg, 0, sizeof cfg);
    cfg.mound_id = MOUND;
    cfg.secret_key = device_sk;
    cfg.controller_public_key = ctl.pk;
    cfg.caps = CAPS; cfg.n_caps = 2;
    cfg.routines = ROUTINES; cfg.n_routines = 1;
    cfg.new_id = next_id; cfg.new_id_ctx = &device_ids;
    cfg.enter_safe_state = enter_safe_state; cfg.safe_state_ctx = &relay;
    CHECK(mm_device_init(&dev, &cfg, error, sizeof error) == 0);
    if (error[0]) printf("  init: %s\n", error);
    executors[0].capability_id = "sense.temp";  executors[0].run = fake_run; executors[0].ctx = &sensor; executors[0].available = 1;
    executors[1].capability_id = "act.relay_1"; executors[1].run = fake_run; executors[1].ctx = &relay;  executors[1].available = 1;
    executors[2].capability_id = "routine.cool"; executors[2].run = fake_run; executors[2].ctx = &cool;  executors[2].available = 1;
    for (i = 0; i < 3; i++) CHECK(mm_kernel_bind_executor(&dev.kernel, &executors[i]) == 0);
    CHECK_STR_EQ("observe_only", mm_device_state(&dev));

    /* beat 1: observe-only; the controller acknowledges and sends a charter */
    ctl.now = T0; ctl.send_charter = 1;
    mm_device_sync(&dev, T0, exchange, &ctl, &out);
    CHECK(out.delivered && out.envelopes_sent == 1 && out.beat_acknowledged && out.downlink_handled == 2);
    CHECK_STR_EQ("chartered", mm_device_state(&dev));
    CHECK(mm_device_queue_depth(&dev) == 0);
    CHECK(mm_authority_lease_alive(&dev.kernel.authority, T0 + 3599));

    /* work: a clamped actuation and a reading, queued */
    CHECK(mm_device_act(&dev, &act_relay, T0 + 5, &record) == 0);
    CHECK_STR_EQ("clamped", record.outcome);
    CHECK(record.n_parameters == 1 && record.parameters[0].value == 30);
    CHECK(mm_device_act(&dev, &sense, T0 + 6, &record) == 0);
    CHECK_STR_EQ("succeeded", record.outcome);
    CHECK(mm_device_queue_depth(&dev) == 2);

    /* beat 2: the drain — three exchanges, each acknowledged; the controller re-delivers the charter (dropped silently) */
    ctl.now = T0 + 15; ctl.send_duplicate_charter = 1;
    mm_device_sync(&dev, T0 + 15, exchange, &ctl, &out);
    CHECK(out.delivered && out.envelopes_sent == 3 && out.beat_acknowledged);
    CHECK(mm_device_queue_depth(&dev) == 0);
    CHECK(mm_device_audit_count(&dev) == 0);                        /* a re-delivery is not an incident */

    /* beat 3: the controller misbehaves four ways in one batch */
    ctl.now = T0 + 30; ctl.send_misrouted = 1; ctl.send_mission_kind = 1; ctl.send_action_record = 1; ctl.send_tampered = 1;
    mm_device_sync(&dev, T0 + 30, exchange, &ctl, &out);
    CHECK(out.delivered && out.beat_acknowledged);
    CHECK(mm_device_queue_depth(&dev) == 1);                        /* the refused_unknown_kind ack for the action_record waits for the next beat */
    CHECK(mm_device_audit_count(&dev) == 3);
    CHECK(strstr(mm_device_audit_at(&dev, 0), "addressed to 'mm-someone-else'") != NULL);
    CHECK(strstr(mm_device_audit_at(&dev, 1), "refused_unknown_kind: 'mission'") != NULL);
    CHECK(strstr(mm_device_audit_at(&dev, 2), "signature does not verify") != NULL);
    CHECK_STR_EQ("chartered", mm_device_state(&dev));               /* the tampered charter changed nothing */
    CHECK(dev.kernel.authority.charter.lease_ttl_s == 3600);

    /* beat 4: a stop arrives; it is handled before anything else, acknowledged, and the safe state entered */
    ctl.now = T0 + 45; ctl.send_stop = 1;
    mm_device_sync(&dev, T0 + 45, exchange, &ctl, &out);
    CHECK(out.delivered);
    CHECK_STR_EQ("stopped", mm_device_state(&dev));
    CHECK(relay.safe_state_entered == 1);
    CHECK(mm_device_queue_depth(&dev) == 1);                        /* the stop's ack */

    /* stopped: actuation refused as `stopped`, sensing continues, both recorded */
    CHECK(mm_device_act(&dev, &act_relay, T0 + 50, &record) == 0);
    CHECK_STR_EQ("stopped", record.outcome);
    CHECK(mm_device_act(&dev, &sense, T0 + 51, &record) == 0);
    CHECK_STR_EQ("succeeded", record.outcome);

    /* beat 5: a charter cannot clear a stop — refused with the reason */
    ctl.now = T0 + 60; ctl.send_charter = 1;
    mm_device_sync(&dev, T0 + 60, exchange, &ctl, &out);
    CHECK(out.delivered);
    CHECK_STR_EQ("stopped", mm_device_state(&dev));
    CHECK(mm_device_queue_depth(&dev) == 1);
    CHECK(strstr(mm_device_queue_at(&dev, 0)->wire, "charter refused: mound is stopped") != NULL);

    /* beat 6: offline — nothing is lost, nothing is acknowledged */
    ctl.now = T0 + 75; ctl.offline = 1;
    mm_device_sync(&dev, T0 + 75, exchange, &ctl, &out);
    CHECK(!out.delivered && out.envelopes_sent == 0);
    CHECK(mm_device_queue_depth(&dev) == 2);
    CHECK(!dev.connected);

    /* beat 7: the link returns; the backlog drains oldest-first */
    ctl.now = T0 + 90; ctl.offline = 0;
    mm_device_sync(&dev, T0 + 90, exchange, &ctl, &out);
    CHECK(out.delivered && out.envelopes_sent == 3 && mm_device_queue_depth(&dev) == 0);
    CHECK(dev.connected);

    /* the operator clears the stop at the board; a short-lease charter arrives; the lease runs out */
    mm_authority_clear_stop(&dev.kernel.authority);
    CHECK_STR_EQ("observe_only", mm_device_state(&dev));
    ctl.now = T0 + 105; ctl.send_charter = 1; ctl.charter_lease_ttl = 30;
    mm_device_sync(&dev, T0 + 105, exchange, &ctl, &out);
    CHECK_STR_EQ("chartered", mm_device_state(&dev));
    CHECK(mm_device_tick(&dev, T0 + 130) == 0);
    CHECK(mm_device_tick(&dev, T0 + 140) == 1);
    CHECK_STR_EQ("quiesced", mm_device_state(&dev));
    CHECK(relay.safe_state_entered == 2);
    CHECK(mm_device_act(&dev, &act_relay, T0 + 141, &record) == 0);
    CHECK_STR_EQ("refused", record.outcome);
    CHECK(strstr(record.detail, "lease_expired") != NULL);

    /* beat 8: the beat says quiesced; the record goes up */
    ctl.now = T0 + 150;
    mm_device_sync(&dev, T0 + 150, exchange, &ctl, &out);
    CHECK(out.delivered && mm_device_queue_depth(&dev) == 0);

    /* the transcript, both ways */
    compare_or_create(&ctl, device_pk_hex);

    /* a full queue refuses to record rather than overwrite unacknowledged proof */
    {
        int published = 0, refused = 0;
        for (i = 0; i < MM_DEVICE_QUEUE + 3; i++) {
            if (mm_device_act(&dev, &sense, T0 + 200 + (int64_t)i, &record) == 0) published++; else refused++;
        }
        CHECK(published == MM_DEVICE_QUEUE && refused == 3);
        CHECK(mm_device_beat(&dev, T0 + 300) == -1);
        CHECK(strstr(mm_device_audit_at(&dev, mm_device_audit_count(&dev) - 1), "uplink queue full") != NULL);
    }
}
