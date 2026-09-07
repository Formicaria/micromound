/*
 * The board layer on the host: mm_app over a fake HAL — an in-memory kv, a scripted HTTP endpoint
 * that plays the controller for enrollment and sync, fake GPIO and ADC, a fixed clock and an RNG
 * whose first 32 bytes are the fixture's device seed. The same scenario a real board goes through:
 * first boot (seed created), no token, an outage, a controller error, enrollment, beats, a charter,
 * probe readings, a relay hold and its release, a reboot that stays enrolled, a stop, a trip.
 *
 * Also checks mm_enroll against enroll-exchange.txt: the request body byte for byte, and every
 * scripted response's verdict and detail line in the host's words.
 */
#include "mm_test.h"
#include "mm_app.h"
#include "mm_ed25519.h"
#include "mm_envelope.h"
#include "mm_format.h"
#include "mm_sha256.h"
#include "mm_time.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static const char MOUND[] = "mm-7f3a0000-0000-4000-8000-000000000001";
static const char CONTROLLER_PK_HEX[] = "29acbae141bccaf0b22e1a94d34d0bc7361e526d0bfe12c89794bc9322966dd7";
static const int64_t T0 = 1786741451LL;   /* 2026-08-14T21:04:11Z */

/* ---- the compiled device: the same tables as test_kernel.c / test_device.c ---------------- */

static const char *const RELAY_PARAMS[] = { "on_s" };
static const mm_param_range RELAY_RANGES[] = { { "on_s", 1, 3600 } };
#define SET(v) { 1, (v) }
#define UNSET { 0, 0 }
static const mm_capability_desc CAPS[] = {
    { "sense.temp", 0, { UNSET, UNSET, UNSET, UNSET, UNSET }, NULL, 0, NULL, 0, NULL, 0, NULL, NULL },
    { "act.relay_1", 1, { SET(60), SET(120), UNSET, UNSET, SET(4) }, RELAY_PARAMS, 1, RELAY_PARAMS, 1, RELAY_RANGES, 1, "on_s", NULL },
};
static const mm_param ON_50 = { "on_s", 50 };
static const mm_schedule_entry SCHEDULE[] = {
    { "sense.temp", NULL, 0, 60 },
    { "act.relay_1", &ON_50, 1, 600 },
};

/* ---- the fake HAL -------------------------------------------------------------------------- */

#define KV_SLOTS 8
#define KV_CAP 256
#define WIRE_CAP 4096

typedef struct kv_entry { char key[32]; uint8_t data[KV_CAP]; size_t n; int present; } kv_entry;

typedef struct fake {
    int64_t now;
    kv_entry kv[KV_SLOTS];
    int kv_set_fails;
    unsigned rng_calls;

    /* enrollment endpoint script */
    int enroll_offline, enroll_status;
    char enroll_body[512];
    int enroll_posts;
    char last_path[64];
    char last_body[2048];

    /* sync endpoint: a controller that verifies, acknowledges, and sends what the script says */
    int sync_offline, sync_status;
    uint8_t ctl_sk[64], ctl_pk[32];
    const uint8_t *device_pk;
    long long seq, acked_through;
    char last_digest[MM_DIGEST_TEXT_LEN + 1];
    int ids;
    int send_charter, send_stop, garbage_downlink;
    int sync_posts, rejected;
    char wire[3][WIRE_CAP];

    /* the board */
    int gpio[48];
    int gpio_writes, gpio_fail_pin;
    double adc[8];
    int adc_fail;
} fake;

static int64_t f_now(void *ctx) { return ((fake *)ctx)->now; }

static int f_random(void *ctx, uint8_t *out, size_t n)
{
    fake *f = (fake *)ctx;
    size_t i;
    if (f->rng_calls++ == 0 && n == 32) { for (i = 0; i < 32; i++) out[i] = (uint8_t)i; return 0; }   /* the fixture seed */
    for (i = 0; i < n; i++) out[i] = (uint8_t)(0xA0 + f->rng_calls * 7 + i);
    return 0;
}

static kv_entry *kv_find(fake *f, const char *key, int create)
{
    size_t i;
    for (i = 0; i < KV_SLOTS; i++) if (f->kv[i].present && strcmp(f->kv[i].key, key) == 0) return &f->kv[i];
    if (!create) return NULL;
    for (i = 0; i < KV_SLOTS; i++) if (!f->kv[i].present) {
        f->kv[i].present = 1;
        snprintf(f->kv[i].key, sizeof f->kv[i].key, "%s", key);
        f->kv[i].n = 0;
        return &f->kv[i];
    }
    return NULL;
}

static int f_kv_get(void *ctx, const char *key, uint8_t *out, size_t cap, size_t *n)
{
    kv_entry *e = kv_find((fake *)ctx, key, 0);
    if (!e || e->n > cap) return -1;
    memcpy(out, e->data, e->n);
    *n = e->n;
    return 0;
}

static int f_kv_set(void *ctx, const char *key, const uint8_t *data, size_t n)
{
    fake *f = (fake *)ctx;
    kv_entry *e;
    if (f->kv_set_fails || n > KV_CAP) return -1;
    e = kv_find(f, key, 1);
    if (!e) return -1;
    memcpy(e->data, data, n);
    e->n = n;
    return 0;
}

static int f_gpio(void *ctx, int pin, int level)
{
    fake *f = (fake *)ctx;
    f->gpio_writes++;
    if (pin < 0 || pin >= 48) return -1;
    if (pin == f->gpio_fail_pin) return -1;
    f->gpio[pin] = level;
    return 0;
}

static int f_gpio_read(void *ctx, int pin, int *level)
{
    fake *f = (fake *)ctx;
    if (pin < 0 || pin >= 48 || pin == f->gpio_fail_pin) return -1;
    *level = f->gpio[pin];
    return 0;
}

static int f_adc(void *ctx, int channel, double *volts)
{
    fake *f = (fake *)ctx;
    if (f->adc_fail || channel < 0 || channel >= 8) return -1;
    *volts = f->adc[channel];
    return 0;
}

/* the controller's downlink: signed by the fixture's controller key, chained like ANTHILL's */
static size_t ctl_send(fake *f, size_t slot, const char *mound, const char *kind, mm_body_writer body, const void *ctx)
{
    mm_envelope e;
    char id[MM_ID_CAP], sent_at[MM_TIME_TEXT_CAP], digest[MM_DIGEST_TEXT_LEN + 1];
    size_t n;
    snprintf(id, sizeof id, "c0000000-0000-4000-8000-%012d", ++f->ids);
    mm_time_format(f->now, sent_at, sizeof sent_at);
    memset(&e, 0, sizeof e);
    e.id = id; e.mound_id = mound; e.seq = f->seq; e.sent_at = sent_at; e.kind = kind;
    e.prev_digest = f->last_digest; e.body = body; e.body_ctx = ctx;
    n = mm_envelope_write_signed(&e, f->ctl_sk, f->wire[slot], WIRE_CAP, digest);
    if (n) { f->seq++; memcpy(f->last_digest, digest, sizeof digest); }
    return n;
}

static void ack_body(mm_json *w, const void *ctx)
{
    mm_ack a;
    memset(&a, 0, sizeof a);
    a.status = "ok"; a.refers_to = ""; a.through_seq = *(const long long *)ctx; a.detail = "";
    mm_body_ack(w, &a);
}

static void charter_body(mm_json *w, const void *ctx)
{
    static const char *const caps[] = { "sense.temp", "act.relay_1" };
    static const char *const required_for[] = { "act.*" };
    mm_limit_entry limits[1];
    mm_charter ch;
    (void)ctx;
    memset(&ch, 0, sizeof ch);
    memset(limits, 0, sizeof limits);
    limits[0].capability = "act.relay_1";
    limits[0].limits.max_on_s.present = 1; limits[0].limits.max_on_s.value = 30;
    ch.charter_id = "c0000000-0000-4000-8000-0000000000c1";
    ch.mound_id = MOUND;
    ch.mission_ref = "mission-0001";
    ch.issued_at = "2026-08-14T21:04:11Z";
    ch.expires_at = "2026-08-15T00:04:11Z";
    ch.lease_ttl_s = 3600;
    ch.action_ceiling = "benign";
    ch.capabilities = caps; ch.n_capabilities = 2;
    ch.limits = limits; ch.n_limits = 1;
    ch.evidence_required_for = required_for; ch.n_evidence_required_for = 1;
    ch.evidence_min_interval_s = 60;
    ch.safe_state = "all_actuators_off";
    ch.sync_interval_s = 15;
    mm_body_charter(w, &ch);
}

static int f_http(void *ctx, const char *path, const char *body, size_t body_len, char *resp, size_t cap, size_t *resp_len, int *status)
{
    fake *f = (fake *)ctx;
    size_t n;

    snprintf(f->last_path, sizeof f->last_path, "%s", path);
    n = body_len < sizeof f->last_body - 1 ? body_len : sizeof f->last_body - 1;
    memcpy(f->last_body, body, n); f->last_body[n] = '\0';
    *resp_len = 0; resp[0] = '\0';

    if (strcmp(path, MM_ENROLL_PATH) == 0) {
        f->enroll_posts++;
        if (f->enroll_offline) return -1;
        *status = f->enroll_status;
        n = strlen(f->enroll_body); if (n > cap - 1) n = cap - 1;
        memcpy(resp, f->enroll_body, n); resp[n] = '\0'; *resp_len = n;
        return 0;
    }
    if (strcmp(path, MM_SYNC_PATH) == 0) {
        mm_envelope_in frame;
        int err, k = 0;
        size_t i, lens[3];
        f->sync_posts++;
        if (f->sync_offline) return -1;
        if (f->sync_status != 200) { *status = f->sync_status; return 0; }
        if (f->garbage_downlink) { *status = 200; snprintf(resp, cap, "{\"not\":\"an array\"}"); *resp_len = strlen(resp); return 0; }
        if (mm_envelope_verify_wire(body, body_len, f->device_pk, NULL) != 0 || mm_envelope_parse(body, body_len, &frame, &err) != 0) {
            f->rejected++;
            *status = 200; snprintf(resp, cap, "[]"); *resp_len = 2;
            return 0;
        }
        if (frame.seq > f->acked_through) f->acked_through = frame.seq;
        lens[k] = ctl_send(f, k, MOUND, MM_KIND_ACK, ack_body, &f->acked_through); k++;
        if (f->send_charter) { lens[k] = ctl_send(f, k, MOUND, MM_KIND_CHARTER, charter_body, NULL); k++; f->send_charter = 0; }
        if (f->send_stop) { mm_stop st; st.reason = "operator stop"; lens[k] = ctl_send(f, k, MOUND, MM_KIND_STOP, mm_body_stop, &st); k++; f->send_stop = 0; }
        n = 0; resp[n++] = '[';
        for (i = 0; i < (size_t)k; i++) {
            if (n + lens[i] + 2 >= cap) break;
            if (i) resp[n++] = ',';
            memcpy(resp + n, f->wire[i], lens[i]); n += lens[i];
        }
        resp[n++] = ']'; resp[n] = '\0';
        *resp_len = n; *status = 200;
        return 0;
    }
    *status = 404;
    return 0;
}

static void fake_init(fake *f)
{
    uint8_t seed[32];
    size_t i;
    memset(f, 0, sizeof *f);
    f->now = T0;
    f->enroll_status = 200;
    f->sync_status = 200;
    f->gpio_fail_pin = -1;
    f->acked_through = -1;
    for (i = 0; i < 32; i++) seed[i] = (uint8_t)(0x20 + i);
    mm_ed25519_seed_keypair(f->ctl_pk, f->ctl_sk, seed);
    f->adc[0] = 0.75;
}

static void hal_bind(mm_hal *hal, fake *f)
{
    hal->ctx = f;
    hal->now = f_now; hal->random_bytes = f_random; hal->http_post_json = f_http;
    hal->kv_get = f_kv_get; hal->kv_set = f_kv_set; hal->gpio_write = f_gpio; hal->gpio_read = f_gpio_read; hal->adc_read = f_adc;
}

static size_t kv_len(fake *f, const char *key) { kv_entry *e = kv_find(f, key, 0); return e ? e->n : (size_t)-1; }

/* ---- enroll-exchange.txt --------------------------------------------------------------------- */

static const char *after_colon(const char *line)
{
    const char *p = strchr(line, ':');
    if (!p) return "";
    p++;
    while (*p == ' ') p++;
    return p;
}

/* One `## case` of the fixture, replayed through mm_enroll_read_response. */
static void replay_case(const mm_enroll_request *req, int status, const char *resp,
                        const char *expect_verdict, const char *expect_detail, const char *expect_result)
{
    mm_enrollment out;
    char detail[MM_REASON_CAP], result[256], key_hex[65], sync[MM_FORMAT_DOUBLE_MAX];
    const char *rbody = strcmp(resp, "(no body)") == 0 ? "" : resp;
    int enrolled = mm_enroll_read_response(req, status, rbody, strlen(rbody), &out, detail, sizeof detail);
    CHECK_STR_EQ(expect_verdict, enrolled ? "enrolled" : "not enrolled");
    CHECK_STR_EQ(expect_detail, detail);
    if (enrolled) {
        mm_hex_lower(out.controller_public_key, 32, key_hex);
        if (out.has_sync_interval) mm_format_double(out.sync_interval_s, sync, sizeof sync); else strcpy(sync, "none");
        snprintf(result, sizeof result, "key=%s mound=%s sync=%s", key_hex, out.controller_mound_id, sync);
        CHECK_STR_EQ(expect_result, result);
    } else {
        CHECK(expect_result[0] == '\0');
    }
}

static void check_enroll_fixture(void)
{
    char path[1024], line[4096];
    FILE *f;
    mm_enroll_request req;
    uint8_t seed[32], sk[64], pk[32];
    static const char *const caps[] = { "sense.temp", "act.relay_1" };
    char body[1024];
    size_t i, n, cases = 0;
    int have_case = 0, status = 0;
    char resp[1024], expect_verdict[32], expect_detail[MM_REASON_CAP], expect_result[256];

    for (i = 0; i < 32; i++) seed[i] = (uint8_t)i;
    mm_ed25519_seed_keypair(pk, sk, seed);
    memset(&req, 0, sizeof req);
    req.token = "tok-0001"; req.mound_id = MOUND; req.device_public_key = pk;
    req.hardware_profile = "sense.temp,act.relay_1"; req.tier = MM_TIER_DETERMINISTIC_CONTROLLER;
    req.capabilities = caps; req.n_capabilities = 2;
    n = mm_enroll_request_body(&req, body, sizeof body);
    CHECK(n > 0);

    snprintf(path, sizeof path, "%s/enroll-exchange.txt", mm_test_golden_dir);
    f = fopen(path, "rb");
    if (!f) { printf("  cannot open %s\n", path); mm_test_failures++; return; }

    resp[0] = expect_verdict[0] = expect_detail[0] = expect_result[0] = '\0';
    while (fgets(line, sizeof line, f)) {
        size_t l = strlen(line);
        while (l > 0 && (line[l - 1] == '\n' || line[l - 1] == '\r')) line[--l] = '\0';
        if (line[0] == '#' && line[1] != '#') continue;
        if (strncmp(line, "path:", 5) == 0) { CHECK_STR_EQ("/micromound/v0/enroll", after_colon(line)); CHECK_STR_EQ("/" MM_ENROLL_PATH, after_colon(line)); continue; }
        if (strncmp(line, "request:", 8) == 0) { CHECK_STR_EQ(after_colon(line), body); continue; }
        if (strncmp(line, "## ", 3) == 0) { have_case = 1; resp[0] = expect_verdict[0] = expect_detail[0] = expect_result[0] = '\0'; continue; }
        if (!have_case) continue;
        if (strncmp(line, "response:", 9) == 0) {
            const char *v = after_colon(line), *sp = strchr(v, ' ');
            status = atoi(v);
            if (sp) snprintf(resp, sizeof resp, "%s", sp + 1); else resp[0] = '\0';
            continue;
        }
        if (strncmp(line, "verdict:", 8) == 0) { snprintf(expect_verdict, sizeof expect_verdict, "%s", after_colon(line)); continue; }
        if (strncmp(line, "result:", 7) == 0) { snprintf(expect_result, sizeof expect_result, "%s", after_colon(line)); continue; }
        if (strncmp(line, "detail:", 7) == 0) { snprintf(expect_detail, sizeof expect_detail, "%s", after_colon(line)); continue; }
        if (line[0] == '\0' && expect_verdict[0]) {
            replay_case(&req, status, resp, expect_verdict, expect_detail, expect_result);
            cases++;
            expect_verdict[0] = '\0';
        }
    }
    if (expect_verdict[0]) { replay_case(&req, status, resp, expect_verdict, expect_detail, expect_result); cases++; }
    fclose(f);
    CHECK(cases == 15);
}

/* ---- the link on its own ------------------------------------------------------------------ */

static void check_link(void)
{
    mm_wire items[2];
    size_t n = 99;
    static const char two[] = " [ {\"kind\":\"ack\"} , {\"a\":[1,2,{\"b\":null}]} ] ";
    static const char three[] = "[{},{},{}]";
    fake f;
    mm_hal hal;
    static mm_link link;
    mm_wire down[MM_DEVICE_BATCH];

    CHECK(mm_link_parse_downlink("", 0, items, 2, &n) == 0 && n == 0);
    CHECK(mm_link_parse_downlink(" \r\n\t", 4, items, 2, &n) == 0 && n == 0);
    CHECK(mm_link_parse_downlink("[]", 2, items, 2, &n) == 0 && n == 0);
    CHECK(mm_link_parse_downlink(two, strlen(two), items, 2, &n) == 0 && n == 2);
    CHECK(items[0].n == 14 && memcmp(items[0].bytes, "{\"kind\":\"ack\"}", 14) == 0);
    CHECK(items[1].n == 22 && memcmp(items[1].bytes, "{\"a\":[1,2,{\"b\":null}]}", 22) == 0);
    CHECK(mm_link_parse_downlink(three, strlen(three), items, 2, &n) == 0 && n == 2);   /* the third is validated, not kept */
    CHECK(mm_link_parse_downlink("{}", 2, items, 2, &n) == -1);
    CHECK(mm_link_parse_downlink("[1,]", 4, items, 2, &n) == -1);
    CHECK(mm_link_parse_downlink("[{}", 3, items, 2, &n) == -1);
    CHECK(mm_link_parse_downlink("[{}] x", 6, items, 2, &n) == -1);

    fake_init(&f);
    hal_bind(&hal, &f);
    mm_link_init(&link, &hal);

    f.sync_offline = 1;
    CHECK(mm_link_exchange(&link, "{}", 2, down, &n) == -1 && n == 0);
    CHECK_STR_EQ("offline: no exchange", link.last_detail);
    CHECK(link.last_status == 0);

    f.sync_offline = 0; f.sync_status = 503;
    CHECK(mm_link_exchange(&link, "{}", 2, down, &n) == -1 && n == 0);
    CHECK_STR_EQ("controller returned HTTP 503", link.last_detail);
    CHECK(link.last_status == 503);

    f.sync_status = 200; f.garbage_downlink = 1;
    CHECK(mm_link_exchange(&link, "{}", 2, down, &n) == -1 && n == 0);
    CHECK_STR_EQ("controller response unreadable", link.last_detail);

    f.garbage_downlink = 0; f.device_pk = f.ctl_pk;   /* an unsigned "{}" is rejected by the fake controller: an empty array */
    CHECK(mm_link_exchange(&link, "{}", 2, down, &n) == 0 && n == 0);
    CHECK_STR_EQ("exchanged; 0 downlink envelope(s)", link.last_detail);
    CHECK(f.rejected == 1);
    CHECK_STR_EQ(MM_SYNC_PATH, f.last_path);
}

/* ---- the whole board ------------------------------------------------------------------------ */

static void configure(mm_app_config *cfg, mm_relay *relay, mm_probe *probe)
{
    memset(cfg, 0, sizeof *cfg);
    cfg->mound_id = MOUND;
    cfg->hardware_profile = "sense.temp,act.relay_1";
    cfg->caps = CAPS; cfg->n_caps = 2;
    cfg->relays = relay; cfg->n_relays = 1;
    cfg->probes = probe; cfg->n_probes = 1;
    cfg->schedule = SCHEDULE; cfg->n_schedule = 2;
}

static void check_board(void)
{
    static fake f;
    static mm_app app;                          /* large */
    mm_hal hal;
    mm_relay relay;
    mm_probe probe;
    mm_app_config cfg;
    char error[256], pk_hex[65], payload[MM_PAYLOAD_CAP];
    uint8_t first_pk[32];

    fake_init(&f);
    hal_bind(&hal, &f);

    /* first boot: the seed is created from the RNG and stored; the relay comes up at its safe level */
    CHECK(mm_relay_init(&relay, &hal, "act.relay_1", 5, 1) == 0);
    CHECK(f.gpio[5] == 0 && f.gpio_writes == 1);
    mm_probe_init(&probe, &hal, "sense.temp", 0, 100, -50, "C");   /* 0.75 V -> 25 C */
    configure(&cfg, &relay, &probe);
    CHECK(mm_app_init(&app, &hal, &cfg, error, sizeof error) == 0);
    if (error[0]) printf("  init: %s\n", error);
    CHECK(kv_len(&f, MM_KV_SEED) == 32);
    mm_hex_lower(app.pk, 32, pk_hex);
    CHECK_STR_EQ("03a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1ddc8664125531b8", pk_hex);
    memcpy(first_pk, app.pk, 32);
    f.device_pk = app.pk;
    CHECK(!app.status.enrolled);
    CHECK(app.sync_interval_s == MM_APP_DEFAULT_SYNC_S);

    /* a zero clock: nothing happens at all */
    f.now = 0;
    mm_app_tick(&app, 0);
    CHECK(f.enroll_posts == 0 && f.sync_posts == 0 && app.status.enroll_attempts == 0);
    f.now = T0;

    /* no token provisioned: the tick says so and does not post */
    mm_app_tick(&app, T0);
    CHECK(f.enroll_posts == 0);
    CHECK_STR_EQ("not enrolled and no enrollment token is provisioned", app.status.last_detail);
    CHECK(app.status.next_enroll_attempt_at == T0 + MM_APP_ENROLL_RETRY_S);
    CHECK(app.status.enroll_attempts == 0);

    /* the token is provisioned, but the controller is offline: retried, token kept */
    f_kv_set(&f, MM_KV_ENROLL_TOKEN, (const uint8_t *)"tok-0001", 8);
    f.enroll_offline = 1;
    mm_app_tick(&app, T0 + 10);                                  /* not due yet */
    CHECK(f.enroll_posts == 0);
    mm_app_tick(&app, T0 + 30);
    CHECK(f.enroll_posts == 1 && app.status.enroll_attempts == 1 && !app.status.enrolled);
    CHECK_STR_EQ("controller unreachable; not enrolled yet", app.status.last_detail);
    CHECK(kv_len(&f, MM_KV_ENROLL_TOKEN) == 8);
    CHECK_STR_EQ(MM_ENROLL_PATH, f.last_path);

    /* the request body is the fixture's, byte for byte */
    {
        static const char expect[] = "{\"token\":\"tok-0001\",\"mound_id\":\"mm-7f3a0000-0000-4000-8000-000000000001\","
            "\"device_public_key\":\"03a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1ddc8664125531b8\","
            "\"hardware_profile\":\"sense.temp,act.relay_1\",\"tier\":\"deterministic_controller\","
            "\"capabilities\":[\"sense.temp\",\"act.relay_1\"],\"protocol_version\":0,\"driver_schemas\":[],"
            "\"features\":[]}";
        CHECK_STR_EQ(expect, f.last_body);
    }

    /* a controller error: deferred, token kept */
    f.enroll_offline = 0; f.enroll_status = 500; strcpy(f.enroll_body, "{\"error\":\"db\"}");
    mm_app_tick(&app, T0 + 60);
    CHECK(f.enroll_posts == 2 && !app.status.enrolled);
    CHECK_STR_EQ("controller returned HTTP 500; enrollment not yet complete", app.status.last_detail);
    CHECK(kv_len(&f, MM_KV_ENROLL_TOKEN) == 8);
    CHECK(f.sync_posts == 0);                                   /* nothing beats before enrollment */

    /* accepted: key and cadence stored, token burned */
    f.enroll_status = 200;
    snprintf(f.enroll_body, sizeof f.enroll_body, "{\"controller_public_key\":\"%s\",\"mound_id\":\"%s\",\"sync_interval_s\":10,\"protocol_version\":0}", CONTROLLER_PK_HEX, MOUND);
    mm_app_tick(&app, T0 + 90);
    CHECK(f.enroll_posts == 3 && app.status.enrolled);
    CHECK_STR_EQ("enrolled (controller asks for a 10s sync cadence)", app.status.last_detail);
    CHECK(kv_len(&f, MM_KV_ENROLL_TOKEN) == 0);
    CHECK(kv_len(&f, MM_KV_CONTROLLER_PK) == 32);
    CHECK(memcmp(app.controller_pk, f.ctl_pk, 32) == 0);
    CHECK(kv_len(&f, MM_KV_SYNC_INTERVAL) == 2 && memcmp(kv_find(&f, MM_KV_SYNC_INTERVAL, 0)->data, "10", 2) == 0);
    CHECK(app.sync_interval_s == 10);
    CHECK(f.sync_posts == 0);                                   /* the enrolling tick ends there; the first beat is the next tick */

    /* first enrolled tick: a beat (observe-only), the controller answers with an ack and a charter;
       then the schedule runs — the probe reads, the relay is energized under the charter's clamp */
    f.send_charter = 1;
    mm_app_tick(&app, T0 + 100);
    CHECK(f.sync_posts == 1 && app.status.beats == 1);
    CHECK_STR_EQ(MM_SYNC_PATH, f.last_path);
    CHECK_STR_EQ("exchanged; 2 downlink envelope(s)", app.status.last_detail);
    CHECK_STR_EQ("chartered", mm_device_state(&app.device));
    CHECK(f.rejected == 0);
    CHECK(app.status.actions == 2);
    CHECK(probe.n == 1);
    CHECK(relay.actuations == 1 && relay.has_hold && f.gpio[5] == 1);
    CHECK(relay.held_until == T0 + 100 + 30);                   /* 50 requested, hw 60, charter 30: held 30 */
    CHECK(mm_device_queue_depth(&app.device) == 2);            /* two action records wait for the next beat */

    /* the reading payload is EvidenceReadings.Create's */
    CHECK(mm_reading_payload(0.75 * 100 - 50, "C", "sense.temp", payload, sizeof payload) > 0);
    CHECK_STR_EQ("{\"value\":25,\"unit\":\"C\",\"capability\":\"sense.temp\"}", payload);

    /* … and it rides INLINE on the record the device queued (PROTOCOL.md §6): the queue holds the probe's
       record with the item, and the relay's record with none — a command is not evidence */
    {
        const char *probe_wire = app.device.queue[app.device.queue_head % MM_DEVICE_QUEUE].wire;
        const char *relay_wire = app.device.queue[(app.device.queue_head + 1) % MM_DEVICE_QUEUE].wire;
        CHECK(strstr(probe_wire, "\"capability\":\"sense.temp\"") != NULL && strstr(relay_wire, "\"capability\":\"act.relay_1\"") != NULL);
        CHECK(strstr(probe_wire, "\"evidence_refs\":[\"e-sense.temp-1\"],\"evidence\":[{\"evidence_id\":\"e-sense.temp-1\",\"type\":\"reading\",") != NULL);
        CHECK(strstr(probe_wire, "\"payload_json\":\"{\\\"value\\\":25,\\\"unit\\\":\\\"C\\\",\\\"capability\\\":\\\"sense.temp\\\"}\",\"content_digest\":\"\"}],") != NULL);
        CHECK(strstr(relay_wire, "\"evidence_refs\":[],\"evidence\":[],") != NULL);
    }

    /* the charter's cadence (15) wins over enrollment's (10) */
    mm_app_tick(&app, T0 + 110);
    CHECK(f.sync_posts == 1 && app.status.beats == 1);
    CHECK(f.gpio[5] == 1);                                      /* still held */
    mm_app_tick(&app, T0 + 115);
    CHECK(f.sync_posts == 4 && app.status.beats == 2);         /* one envelope per exchange: the beat and two records */
    CHECK(mm_device_queue_depth(&app.device) == 0);            /* the records drained, acknowledged */
    CHECK_STR_EQ("exchanged; 1 downlink envelope(s)", app.status.last_detail);

    /* the hold elapses: released at the top of the tick, before anything else */
    mm_app_tick(&app, T0 + 129);
    CHECK(f.gpio[5] == 1 && relay.has_hold);
    mm_app_tick(&app, T0 + 130);
    CHECK(f.gpio[5] == 0 && !relay.has_hold && !relay.release_failed);
    CHECK(!app.status.tripped);

    /* the probe on its period: 60 s later it reads again; the relay's 600 s period has not come */
    mm_app_tick(&app, T0 + 160);
    CHECK(probe.n == 2 && relay.actuations == 1);

    /* a failed sensor read is a fault, never a zero */
    f.adc_fail = 1;
    mm_app_tick(&app, T0 + 220);
    CHECK(probe.n == 2);
    f.adc_fail = 0;

    /* an outage mid-session: the beat fails, the queue keeps the records, the device stays chartered */
    f.sync_offline = 1;
    mm_app_tick(&app, T0 + 235);
    CHECK_STR_EQ("offline: no exchange", app.status.last_detail);
    CHECK(mm_device_queue_depth(&app.device) > 0);
    CHECK_STR_EQ("chartered", mm_device_state(&app.device));
    f.sync_offline = 0;
    mm_app_tick(&app, T0 + 250);
    CHECK(mm_device_queue_depth(&app.device) == 0);

    /* reboot: the same kv, a new app — same identity, still enrolled, enrollment's cadence until chartered */
    {
        static mm_app again;
        mm_relay relay2;
        mm_probe probe2;
        mm_app_config cfg2;
        f.rng_calls = 100;                                      /* the RNG would now produce a different seed — it must not be asked */
        CHECK(mm_relay_init(&relay2, &hal, "act.relay_1", 5, 1) == 0);
        mm_probe_init(&probe2, &hal, "sense.temp", 0, 100, -50, "C");
        configure(&cfg2, &relay2, &probe2);
        CHECK(mm_app_init(&again, &hal, &cfg2, error, sizeof error) == 0);
        CHECK(memcmp(again.pk, first_pk, 32) == 0);
        CHECK(again.status.enrolled);
        CHECK(memcmp(again.controller_pk, f.ctl_pk, 32) == 0);
        CHECK(again.sync_interval_s == 10);
        CHECK_STR_EQ("observe_only", mm_device_state(&again.device));   /* a charter is not persisted; the controller re-issues it */
        f.device_pk = again.pk;
        f.send_charter = 1;
        mm_app_tick(&again, T0 + 300);
        CHECK_STR_EQ("chartered", mm_device_state(&again.device));
        CHECK(relay2.has_hold && f.gpio[5] == 1);

        /* stop: the safe state releases the relay; further schedule requests are refused, not actuated */
        f.send_stop = 1;
        mm_app_tick(&again, T0 + 315);
        CHECK_STR_EQ("stopped", mm_device_state(&again.device));
        CHECK(!relay2.has_hold && f.gpio[5] == 0);
        CHECK(!again.status.tripped);
        mm_app_tick(&again, T0 + 900);                          /* the relay's period has come; the kernel refuses */
        CHECK(relay2.actuations == 1 && f.gpio[5] == 0);
        CHECK(again.status.actions > 2);                        /* the refusals were still recorded */
    }

    /* a trip: a relay whose release write fails stops the mound */
    {
        static mm_app third;
        mm_relay relay3;
        mm_probe probe3;
        mm_app_config cfg3;
        CHECK(mm_relay_init(&relay3, &hal, "act.relay_1", 5, 1) == 0);
        mm_probe_init(&probe3, &hal, "sense.temp", 0, 100, -50, "C");
        configure(&cfg3, &relay3, &probe3);
        CHECK(mm_app_init(&third, &hal, &cfg3, error, sizeof error) == 0);
        f.device_pk = third.pk;
        f.send_charter = 1;
        mm_app_tick(&third, T0 + 1000);
        CHECK(relay3.has_hold && f.gpio[5] == 1 && relay3.held_until == T0 + 1030);
        f.gpio_fail_pin = 5;
        mm_app_tick(&third, T0 + 1030);
        CHECK(relay3.release_failed && relay3.has_hold);        /* the hold stays pending */
        CHECK(third.status.tripped);
        CHECK_STR_EQ("stopped", mm_device_state(&third.device));
        CHECK(f.sync_posts > 0);                                /* the beat still went out after the trip: the controller hears it stopped */
        f.gpio_fail_pin = -1;
        mm_app_tick(&third, T0 + 1031);
        CHECK(!relay3.has_hold && f.gpio[5] == 0);              /* the retry releases it; the trip stands */
        CHECK(third.status.tripped);
        CHECK_STR_EQ("stopped", mm_device_state(&third.device));
    }

    /* a definite refusal burns the token; a fresh device with no identity storage refuses to run */
    {
        static fake g;
        static mm_app fourth;
        mm_hal hal2;
        mm_relay relay4;
        mm_probe probe4;
        mm_app_config cfg4;
        fake_init(&g);
        hal_bind(&hal2, &g);
        CHECK(mm_relay_init(&relay4, &hal2, "act.relay_1", 5, 1) == 0);
        mm_probe_init(&probe4, &hal2, "sense.temp", 0, 100, -50, "C");
        configure(&cfg4, &relay4, &probe4);
        CHECK(mm_app_init(&fourth, &hal2, &cfg4, error, sizeof error) == 0);
        f_kv_set(&g, MM_KV_ENROLL_TOKEN, (const uint8_t *)"tok-used", 8);
        g.enroll_status = 409; strcpy(g.enroll_body, "{\"accepted\":false,\"reason\":\"token already used\"}");
        mm_app_tick(&fourth, T0);
        CHECK(g.enroll_posts == 1 && !fourth.status.enrolled);
        CHECK_STR_EQ("enrollment refused: HTTP 409 \xe2\x80\x94 token already used", fourth.status.last_detail);
        CHECK(kv_len(&g, MM_KV_ENROLL_TOKEN) == 0);
        mm_app_tick(&fourth, T0 + 30);
        CHECK(g.enroll_posts == 1);                              /* nothing left to try with */
        CHECK_STR_EQ("not enrolled and no enrollment token is provisioned", fourth.status.last_detail);

        fake_init(&g);
        g.kv_set_fails = 1;
        CHECK(mm_app_init(&fourth, &hal2, &cfg4, error, sizeof error) == -1);
        CHECK_STR_EQ("the device seed could not be stored; an identity that does not survive a reboot is not one", error);
    }
}

void test_board(void)
{
    check_enroll_fixture();
    check_link();
    check_board();
}
