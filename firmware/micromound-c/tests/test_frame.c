/*
 * The Pi↔ESP32 link: mm_frame against link-frames.txt (every frame the host froze, encoded and
 * decoded byte for byte), the decoder's drop-and-resync rules, and mm_serial — the HAL's
 * http_post_json over a fake byte pipe whose far end is a scripted bridge.
 */
#include "mm_test.h"
#include "mm_frame.h"
#include "mm_serial.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- hex helpers ---------------------------------------------------------------------------- */

static size_t unhex(const char *hex, uint8_t *out, size_t cap)
{
    size_t n = 0;
    while (hex[0] && hex[1] && n < cap) {
        unsigned v;
        if (sscanf(hex, "%2x", &v) != 1) break;
        out[n++] = (uint8_t)v;
        hex += 2;
    }
    return n;
}

static const char *after_colon(const char *line)
{
    const char *p = strchr(line, ':');
    if (!p) return "";
    p++;
    while (*p == ' ') p++;
    return p;
}

/* ---- link-frames.txt ------------------------------------------------------------------------ */

static void check_fixture(void)
{
    char path[1024], line[4096];
    FILE *f;
    char type[16] = "", req_path[128] = "", body[1024] = "";
    int seq = 0, status = 0, cases = 0;
    uint8_t expect_payload[2048], expect_frame[2048], built_payload[2048], built_frame[2048];
    size_t n_payload = 0, n_frame = 0;

    CHECK(mm_crc32(0, (const uint8_t *)"123456789", 9) == 0xCBF43926u);
    CHECK(mm_crc32(0, (const uint8_t *)"", 0) == 0);

    snprintf(path, sizeof path, "%s/link-frames.txt", mm_test_golden_dir);
    f = fopen(path, "rb");
    if (!f) { printf("  cannot open %s\n", path); mm_test_failures++; return; }

    while (fgets(line, sizeof line, f)) {
        size_t l = strlen(line);
        while (l > 0 && (line[l - 1] == '\n' || line[l - 1] == '\r')) line[--l] = '\0';
        if (line[0] == '#' && line[1] != '#') continue;
        if (strncmp(line, "crc32:", 6) == 0) {
            const char *v = after_colon(line);
            uint8_t in[64]; unsigned want; size_t n = 0;
            if (strncmp(v, "(empty)", 7) != 0) n = unhex(v, in, sizeof in);
            CHECK(sscanf(strstr(v, "-> ") + 3, "%x", &want) == 1);
            CHECK(mm_crc32(0, in, n) == want);
            continue;
        }
        if (strncmp(line, "## ", 3) == 0) { type[0] = req_path[0] = body[0] = '\0'; seq = status = 0; n_payload = n_frame = 0; continue; }
        if (strncmp(line, "type:", 5) == 0) { snprintf(type, sizeof type, "%s", after_colon(line)); continue; }
        if (strncmp(line, "seq:", 4) == 0) { seq = atoi(after_colon(line)); continue; }
        if (strncmp(line, "path:", 5) == 0) { snprintf(req_path, sizeof req_path, "%s", after_colon(line)); continue; }
        if (strncmp(line, "status:", 7) == 0) { status = atoi(after_colon(line)); continue; }
        if (strncmp(line, "body:", 5) == 0) { snprintf(body, sizeof body, "%s", after_colon(line)); continue; }
        if (strncmp(line, "payload:", 8) == 0) { n_payload = unhex(after_colon(line), expect_payload, sizeof expect_payload); continue; }
        if (strncmp(line, "frame:", 6) == 0) {
            size_t bn, fn;
            mm_frame_decoder d;
            size_t i;
            int completed = 0;
            n_frame = unhex(after_colon(line), expect_frame, sizeof expect_frame);
            cases++;

            /* encode from the case's parts */
            if (strcmp(type, "request") == 0) bn = mm_frame_request_payload(req_path, body, strlen(body), built_payload, sizeof built_payload);
            else bn = mm_frame_response_payload(status, body, strlen(body), built_payload, sizeof built_payload);
            CHECK(bn == n_payload);
            CHECK_MEM_EQ(expect_payload, built_payload, n_payload);
            fn = mm_frame_encode(strcmp(type, "request") == 0 ? MM_FRAME_REQUEST : MM_FRAME_RESPONSE, (uint8_t)seq, built_payload, bn, built_frame, sizeof built_frame);
            CHECK(fn == n_frame);
            CHECK_MEM_EQ(expect_frame, built_frame, n_frame);

            /* decode the frozen frame */
            mm_frame_decoder_init(&d);
            for (i = 0; i < n_frame; i++) if (mm_frame_feed(&d, expect_frame[i])) completed++;
            CHECK(completed == 1);
            CHECK(d.type == (strcmp(type, "request") == 0 ? MM_FRAME_REQUEST : MM_FRAME_RESPONSE) && d.seq == (uint8_t)seq);
            CHECK(d.payload_len == n_payload);
            CHECK_MEM_EQ(expect_payload, d.payload, n_payload);
            if (strcmp(type, "request") == 0) {
                const uint8_t *p, *b; size_t pl, bl;
                CHECK(mm_frame_parse_request(d.payload, d.payload_len, &p, &pl, &b, &bl) == 0);
                CHECK(pl == strlen(req_path) && memcmp(p, req_path, pl) == 0);
                CHECK(bl == strlen(body) && memcmp(b, body, bl) == 0);
            } else {
                const uint8_t *b; size_t bl; int st;
                CHECK(mm_frame_parse_response(d.payload, d.payload_len, &st, &b, &bl) == 0);
                CHECK(st == status);
                CHECK(bl == strlen(body) && memcmp(b, body, bl) == 0);
            }
        }
    }
    fclose(f);
    CHECK(cases == 11);
}

/* ---- the decoder's rules -------------------------------------------------------------------- */

static void check_decoder(void)
{
    uint8_t payload[64], good[128], bad[128];
    size_t pn = mm_frame_request_payload("micromound/v0/sync", "{}", 2, payload, sizeof payload);
    size_t gn = mm_frame_encode(MM_FRAME_REQUEST, 7, payload, pn, good, sizeof good);
    mm_frame_decoder d;
    size_t i;
    int got;

    CHECK(pn == 21 && gn == pn + MM_FRAME_OVERHEAD);
    mm_frame_decoder_init(&d);

    /* garbage, then a good frame */
    CHECK(!mm_frame_feed(&d, 'x') && !mm_frame_feed(&d, 'x') && !mm_frame_feed(&d, '?'));
    for (got = 0, i = 0; i < gn; i++) got += mm_frame_feed(&d, good[i]);
    CHECK(got == 1 && d.seq == 7 && d.type == MM_FRAME_REQUEST && d.payload_len == pn);

    /* a corrupted CRC: dropped, counted; the next frame still decodes */
    memcpy(bad, good, gn); bad[gn - 1] ^= 0xFF;
    for (got = 0, i = 0; i < gn; i++) got += mm_frame_feed(&d, bad[i]);
    CHECK(got == 0 && d.dropped_crc == 1);
    for (got = 0, i = 0; i < gn; i++) got += mm_frame_feed(&d, good[i]);
    CHECK(got == 1 && d.frames == 2);

    /* the wrong version and an oversize length: dropped at the header */
    memcpy(bad, good, gn); bad[2] = 2;
    for (got = 0, i = 0; i < gn; i++) got += mm_frame_feed(&d, bad[i]);
    CHECK(got == 0 && d.dropped_version == 1);
    memcpy(bad, good, gn); bad[5] = 0xFF; bad[6] = 0xFF;
    for (got = 0, i = 0; i < gn; i++) got += mm_frame_feed(&d, bad[i]);
    CHECK(got == 0 && d.dropped_oversize == 1);

    /* joined mid-frame: the tail of one, then a whole one */
    for (got = 0, i = 10; i < gn; i++) got += mm_frame_feed(&d, good[i]);
    for (i = 0; i < gn; i++) got += mm_frame_feed(&d, good[i]);
    CHECK(got == 1 && d.frames == 3);

    /* a lone magic byte before a frame costs a version drop and a resync, never the frame */
    mm_frame_feed(&d, 'M');
    for (got = 0, i = 0; i < gn; i++) got += mm_frame_feed(&d, good[i]);
    CHECK(got == 1 && d.frames == 4 && d.dropped_version == 2);

    /* two frames back to back, one byte at a time */
    for (got = 0, i = 0; i < gn; i++) got += mm_frame_feed(&d, good[i]);
    for (i = 0; i < gn; i++) got += mm_frame_feed(&d, good[i]);
    CHECK(got == 2 && d.frames == 6);

    /* a payload the frame cannot carry is refused at encode time */
    {
        static uint8_t big[MM_FRAME_MAX_PAYLOAD + 1];
        static uint8_t out[MM_FRAME_MAX_PAYLOAD + MM_FRAME_OVERHEAD + 8];
        CHECK(mm_frame_encode(MM_FRAME_RESPONSE, 1, big, sizeof big, out, sizeof out) == 0);
        CHECK(mm_frame_encode(MM_FRAME_RESPONSE, 1, big, MM_FRAME_MAX_PAYLOAD, out, sizeof out) == MM_FRAME_MAX_PAYLOAD + MM_FRAME_OVERHEAD);
        CHECK(mm_frame_encode(MM_FRAME_RESPONSE, 1, big, 10, out, 12) == 0);   /* does not fit the buffer */
    }
    CHECK(mm_frame_request_payload("", "{}", 2, payload, sizeof payload) == 0);
    CHECK(mm_frame_request_payload("a\nb", "{}", 2, payload, sizeof payload) == 0);
    {
        const uint8_t *b; size_t bl; int st;
        CHECK(mm_frame_parse_response((const uint8_t *)"abc\n", 4, &st, &b, &bl) == -1);
        CHECK(mm_frame_parse_response((const uint8_t *)"1234\n", 5, &st, &b, &bl) == -1);
        CHECK(mm_frame_parse_response((const uint8_t *)"200", 3, &st, &b, &bl) == -1);
        CHECK(mm_frame_parse_response((const uint8_t *)"0\n", 2, &st, &b, &bl) == 0 && st == 0 && bl == 0);
    }
}

/* ---- mm_serial over a fake pipe with a scripted bridge -------------------------------------- */

typedef struct pipe_bridge {
    /* what the device wrote, decoded by the bridge */
    mm_frame_decoder decoder;
    /* what the bridge queued for the device to read */
    uint8_t inbound[4 * (MM_FRAME_OVERHEAD + 512)];
    size_t inbound_len, inbound_read;
    /* the script */
    int silent;                      /* answer nothing: the device must time out */
    int status;                      /* the status to answer with */
    const char *body;
    int stale_first;                 /* answer once with the wrong seq before the right one */
    int64_t epoch;
    int requests;
    char last_path[64];
    char last_body[512];
} pipe_bridge;

static void bridge_queue(pipe_bridge *b, uint8_t seq, int status, const char *body)
{
    uint8_t payload[600];
    size_t n = mm_frame_response_payload(status, body, strlen(body), payload, sizeof payload);
    if (b->inbound_read == b->inbound_len) b->inbound_read = b->inbound_len = 0;   /* everything queued so far was read */
    b->inbound_len += mm_frame_encode(MM_FRAME_RESPONSE, seq, payload, n, b->inbound + b->inbound_len, sizeof b->inbound - b->inbound_len);
}

static int pipe_write(void *ctx, const uint8_t *bytes, size_t n)
{
    pipe_bridge *b = (pipe_bridge *)ctx;
    size_t i;
    for (i = 0; i < n; i++) {
        if (!mm_frame_feed(&b->decoder, bytes[i])) continue;
        if (b->decoder.type != MM_FRAME_REQUEST) continue;
        {
            const uint8_t *p, *body; size_t pl, bl;
            b->requests++;
            if (mm_frame_parse_request(b->decoder.payload, b->decoder.payload_len, &p, &pl, &body, &bl) != 0) continue;
            snprintf(b->last_path, sizeof b->last_path, "%.*s", (int)pl, (const char *)p);
            snprintf(b->last_body, sizeof b->last_body, "%.*s", (int)bl, (const char *)body);
            if (b->silent) continue;
            if (b->stale_first) { bridge_queue(b, (uint8_t)(b->decoder.seq + 100), 200, "stale"); b->stale_first = 0; }
            if (strcmp(b->last_path, MM_LINK_TIME_PATH) == 0) {
                char t[64]; snprintf(t, sizeof t, "{\"epoch_s\":%lld}", (long long)b->epoch);
                bridge_queue(b, b->decoder.seq, 200, t);
            } else {
                bridge_queue(b, b->decoder.seq, b->status, b->body ? b->body : "");
            }
        }
    }
    return 0;
}

static int pipe_read_byte(void *ctx, uint8_t *out, int timeout_ms)
{
    pipe_bridge *b = (pipe_bridge *)ctx;
    (void)timeout_ms;
    if (b->inbound_read >= b->inbound_len) { b->inbound_read = b->inbound_len = 0; return 0; }   /* nothing more: a timeout */
    *out = b->inbound[b->inbound_read++];
    return 1;
}

static void check_serial(void)
{
    static pipe_bridge bridge;
    static mm_serial_link link;
    mm_serial_io io;
    char resp[512];
    size_t n = 0;
    int status = 0;

    memset(&bridge, 0, sizeof bridge);
    mm_frame_decoder_init(&bridge.decoder);
    bridge.status = 200; bridge.body = "[{\"v\":0}]"; bridge.epoch = 1786741451LL;
    io.ctx = &bridge; io.write = pipe_write; io.read_byte = pipe_read_byte;
    mm_serial_init(&link, &io);

    /* a beat: the request reaches the bridge byte for byte; the answer comes back with its status */
    CHECK(mm_serial_post_json(&link, "micromound/v0/sync", "{\"v\":0,\"sig\":\"ed25519:00\"}", 26, resp, sizeof resp, &n, &status) == 0);
    CHECK_STR_EQ("micromound/v0/sync", bridge.last_path);
    CHECK_STR_EQ("{\"v\":0,\"sig\":\"ed25519:00\"}", bridge.last_body);
    CHECK(status == 200 && n == 9);
    CHECK_STR_EQ("[{\"v\":0}]", resp);
    CHECK(link.seq == 1 && link.requests == 1);

    /* a 4xx and a 5xx travel as themselves */
    bridge.status = 409; bridge.body = "{\"accepted\":false,\"reason\":\"token already used\"}";
    CHECK(mm_serial_post_json(&link, "micromound/v0/enroll", "{}", 2, resp, sizeof resp, &n, &status) == 0 && status == 409);
    CHECK_STR_EQ("{\"accepted\":false,\"reason\":\"token already used\"}", resp);
    bridge.status = 500; bridge.body = "";
    CHECK(mm_serial_post_json(&link, "micromound/v0/enroll", "{}", 2, resp, sizeof resp, &n, &status) == 0 && status == 500 && n == 0);

    /* the bridge could not exchange: status 0 is offline, -1 to the HAL's caller */
    bridge.status = 0;
    CHECK(mm_serial_post_json(&link, "micromound/v0/sync", "{}", 2, resp, sizeof resp, &n, &status) == -1);

    /* a bridge that never answers: a timeout, offline, and the next request is not confused by it */
    bridge.silent = 1;
    CHECK(mm_serial_post_json(&link, "micromound/v0/sync", "{}", 2, resp, sizeof resp, &n, &status) == -1);
    CHECK(link.timeouts == 1);
    bridge.silent = 0; bridge.status = 200; bridge.body = "[]";

    /* a stale answer (another seq) is skipped; the right one is taken */
    bridge.stale_first = 1;
    CHECK(mm_serial_post_json(&link, "micromound/v0/sync", "{}", 2, resp, sizeof resp, &n, &status) == 0 && status == 200);
    CHECK_STR_EQ("[]", resp);
    CHECK(link.stale_responses == 1);

    /* the time */
    CHECK(mm_serial_time(&link) == 1786741451LL);
    CHECK_STR_EQ(MM_LINK_TIME_PATH, bridge.last_path);
    bridge.silent = 1;
    CHECK(mm_serial_time(&link) == 0);
    bridge.silent = 0;

    /* a response body longer than the caller's buffer is truncated, never overrun */
    bridge.body = "[{\"a\":\"0123456789012345678901234567890123456789\"}]";
    CHECK(mm_serial_post_json(&link, "micromound/v0/sync", "{}", 2, resp, 16, &n, &status) == 0 && n == 15 && resp[15] == '\0');

    /* seq wraps through 0 and keeps matching */
    { int i; bridge.body = "[]"; for (i = 0; i < 260; i++) CHECK(mm_serial_post_json(&link, "micromound/v0/sync", "{}", 2, resp, sizeof resp, &n, &status) == 0); }
    CHECK(link.requests == 269);
}

void test_frame(void)
{
    check_fixture();
    check_decoder();
    check_serial();
}
