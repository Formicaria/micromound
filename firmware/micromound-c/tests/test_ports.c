/*
 * The board as a port server (mm_ports): every request the host's LinkPorts client can send, the
 * board's exact answer, the hardware tier the board keeps for itself (max_on_s released by the board),
 * the link watchdog, the trip, and the frames around it. The exchange is written to
 * port-exchange.txt on first run and compared afterwards; the C# LinkPortsTests reads the same file
 * and parses every `resp:` with the Pi's client, and checks its own `req:` bodies against these.
 */
#include "mm_test.h"
#include "mm_frame.h"
#include "mm_ports.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct fake_board {
    int gpio[48];
    int gpio_fail_pin;
    double adc[8];
    int adc_fail;
} fake_board;

static int fb_gpio(void *ctx, int pin, int level)
{
    fake_board *b = (fake_board *)ctx;
    if (pin < 0 || pin >= 48 || pin == b->gpio_fail_pin) return -1;
    b->gpio[pin] = level;
    return 0;
}

static int fb_gpio_read(void *ctx, int pin, int *level)
{
    fake_board *b = (fake_board *)ctx;
    if (pin < 0 || pin >= 48 || pin == b->gpio_fail_pin) return -1;
    *level = b->gpio[pin];
    return 0;
}

static int fb_adc(void *ctx, int channel, double *volts)
{
    fake_board *b = (fake_board *)ctx;
    if (b->adc_fail || channel < 0 || channel >= 8) return -1;
    *volts = b->adc[channel];
    return 0;
}

static int64_t fb_now(void *ctx) { (void)ctx; return 0; }
static int fb_random(void *ctx, uint8_t *out, size_t n) { (void)ctx; memset(out, 0, n); return 0; }

/* ---- the transcript ---------------------------------------------------------------------- */

#define MAX_LINES 96
#define LINE_CAP 1024
static char lines[MAX_LINES][LINE_CAP];
static size_t n_lines;

static void transcript(const char *tag, const char *text)
{
    if (n_lines >= MAX_LINES) return;
    snprintf(lines[n_lines++], LINE_CAP, "%s%.1000s", tag, text);
}

/* Sends one request through mm_ports_handle, records it, and returns the status. */
static int ask(mm_ports *p, const char *path, const char *body, int64_t now, char *resp, size_t cap)
{
    size_t n = 0;
    int status;
    char line[LINE_CAP];
    snprintf(line, sizeof line, "%s %s", path, body);
    transcript("req:  ", line);
    status = mm_ports_handle(p, path, strlen(path), body, strlen(body), now, resp, cap, &n);
    snprintf(line, sizeof line, "%d %s", status, resp);
    transcript("resp: ", line);
    return status;
}

static void compare_or_create(void)
{
    char path[1024], line[LINE_CAP];
    FILE *f;
    size_t i, matched = 0;
    int mismatch = 0;

    snprintf(path, sizeof path, "%s/port-exchange.txt", mm_test_golden_dir);
    f = fopen(path, "rb");
    if (!f) {
        f = fopen(path, "wb");
        if (!f) { printf("  cannot create %s\n", path); mm_test_failures++; return; }
        fprintf(f, "# MICROMOUND port exchange — transcript fixture (PROTOCOL.md §12, port requests)\n#\n");
        fprintf(f, "# Written by firmware/micromound-c/tests/test_ports.c: the board as a port server (mm_ports) answering the\n");
        fprintf(f, "# requests a Pi's LinkPorts client sends. `req:` is `<path> <body>`, `resp:` is `<status> <body>`. The C test\n");
        fprintf(f, "# compares itself against this file; the C# LinkPortsTests parses every `resp:` with the Pi's client and\n");
        fprintf(f, "# checks the client's own request bodies against the `req:` lines. A change here is a protocol change.\n\n");
        for (i = 0; i < n_lines; i++) fprintf(f, "%s\n", lines[i]);
        fclose(f);
        printf("  transcript created at %s — review it, then run again; the C# LinkPortsTests reads it too\n", path);
        mm_test_failures++;
        return;
    }
    while (fgets(line, sizeof line, f)) {
        size_t n = strlen(line);
        while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r')) line[--n] = '\0';
        if (line[0] == '#' || line[0] == '\0') continue;
        if (matched < n_lines) {
            mm_test_checks++;
            if (strcmp(line, lines[matched]) != 0) {
                mm_test_failures++;
                if (!mismatch) printf("  FAIL transcript line %d:\n    expected: %.200s\n    actual:   %.200s\n", (int)matched + 1, line, lines[matched]);
                mismatch = 1;
            }
        }
        matched++;
    }
    fclose(f);
    CHECK(matched == n_lines);
}

/* ---- the scenario -------------------------------------------------------------------------- */

void test_ports(void)
{
    static fake_board board;
    static mm_ports ports;
    mm_hal hal;
    char resp[MM_PORTS_RESPONSE_CAP];

    memset(&board, 0, sizeof board);
    board.gpio_fail_pin = -1;
    board.adc[0] = 0.75;
    memset(&hal, 0, sizeof hal);
    hal.ctx = &board; hal.now = fb_now; hal.random_bytes = fb_random; hal.gpio_write = fb_gpio;
    hal.gpio_read = fb_gpio_read; hal.adc_read = fb_adc;
    n_lines = 0;

    /* bring-up: pins come up SAFE (pin 6 is active-low, so its safe level is high) */
    mm_ports_init(&ports, &hal, "sense.temp,act.relay_1,act.relay_2", "bench-1", 60);
    board.gpio[5] = 1; board.gpio[6] = 0;
    CHECK(mm_ports_add_pin(&ports, 5, 1, 30) == 0 && board.gpio[5] == 0);
    CHECK(mm_ports_add_pin(&ports, 6, 0, 0) == 0 && board.gpio[6] == 1);
    CHECK(mm_ports_add_pin(&ports, 5, 1, 30) == -1);                   /* a duplicate */
    CHECK(mm_ports_add_channel(&ports, 0) == 0 && mm_ports_add_channel(&ports, 0) == -1);
    board.gpio[12] = 1;                                                /* an active-low limit switch, open: the line is pulled high */
    CHECK(mm_ports_add_input(&ports, 12, 0) == 0 && ports.n_inputs == 1);
    CHECK(mm_ports_add_input(&ports, 12, 0) == -1);                    /* a duplicate */
    CHECK(mm_ports_add_input(&ports, 5, 1) == -1);                     /* already an output: one line, one direction */
    CHECK(mm_ports_add_pin(&ports, 12, 1, 10) == -1);                  /* and the other way round */
    board.gpio_fail_pin = 9;
    CHECK(mm_ports_add_pin(&ports, 9, 1, 10) == -1 && ports.n_pins == 2);   /* a pin that will not drive safe is not offered */
    CHECK(mm_ports_add_input(&ports, 9, 1) == -1 && ports.n_inputs == 1);   /* nor a line that will not read */
    board.gpio_fail_pin = -1;

    /* discovery */
    CHECK(ask(&ports, MM_PORTS_PATH_HELLO, "{}", 100, resp, sizeof resp) == 200);
    CHECK_STR_EQ("{\"profile\":\"sense.temp,act.relay_1,act.relay_2\",\"firmware\":\"bench-1\",\"watchdog_s\":60,\"tripped\":false,"
                 "\"pins\":[{\"pin\":5,\"active_high\":true,\"max_on_s\":30,\"level\":false},{\"pin\":6,\"active_high\":false,\"max_on_s\":0,\"level\":false}],"
                 "\"inputs\":[{\"pin\":12,\"active_high\":false,\"level\":false}],"
                 "\"channels\":[0]}", resp);
    CHECK(ask(&ports, MM_PORTS_PATH_HELLO, "", 100, resp, sizeof resp) == 200);   /* an empty body reads as {} */

    /* a write, a read; the board's own max_on_s releases the pin without being asked */
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":5,\"level\":true}", 100, resp, sizeof resp) == 200);
    CHECK_STR_EQ("{\"pin\":5,\"level\":true}", resp);
    CHECK(board.gpio[5] == 1 && ports.pins[0].held_until == 130);
    CHECK(ask(&ports, MM_PORTS_PATH_READ, "{\"channel\":0}", 101, resp, sizeof resp) == 200);
    CHECK_STR_EQ("{\"channel\":0,\"volts\":0.75}", resp);
    CHECK(mm_ports_service(&ports, 129) == 0 && board.gpio[5] == 1);
    CHECK(mm_ports_service(&ports, 130) == 0 && board.gpio[5] == 0 && ports.pins[0].auto_releases == 1 && !ports.pins[0].active);
    CHECK(ask(&ports, MM_PORTS_PATH_HELLO, "{}", 131, resp, sizeof resp) == 200);
    CHECK(strstr(resp, "\"pin\":5,\"active_high\":true,\"max_on_s\":30,\"level\":false") != NULL);

    /* the active-low pin, no bound: stays until released */
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":6,\"level\":true}", 140, resp, sizeof resp) == 200 && board.gpio[6] == 0);
    CHECK(ports.pins[1].held_until == 0);
    CHECK(mm_ports_service(&ports, 141) == 0 && board.gpio[6] == 0);
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":6,\"level\":false}", 142, resp, sizeof resp) == 200 && board.gpio[6] == 1);
    CHECK_STR_EQ("{\"pin\":6,\"level\":false}", resp);

    /* refusals, in the host's words */
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":7,\"level\":true}", 143, resp, sizeof resp) == 404);
    CHECK_STR_EQ("{\"error\":\"pin 7 is not a port of this board\"}", resp);
    CHECK(ask(&ports, MM_PORTS_PATH_READ, "{\"channel\":3}", 143, resp, sizeof resp) == 404);
    CHECK_STR_EQ("{\"error\":\"channel 3 is not a port of this board\"}", resp);
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":5}", 143, resp, sizeof resp) == 400);
    CHECK(ask(&ports, MM_PORTS_PATH_READ, "{}", 143, resp, sizeof resp) == 400);
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":\"five\",\"level\":true}", 143, resp, sizeof resp) == 400);
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "[5]", 143, resp, sizeof resp) == 400);
    CHECK(ask(&ports, "micromound/link/ports/reboot", "{}", 143, resp, sizeof resp) == 404);
    CHECK_STR_EQ("{\"error\":\"not a port request\"}", resp);
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":5,\"level\":true,\"future\":{\"x\":[1]}}", 144, resp, sizeof resp) == 200);   /* unknown members skipped */
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":5,\"level\":false}", 145, resp, sizeof resp) == 200);
    board.adc_fail = 1;
    CHECK(ask(&ports, MM_PORTS_PATH_READ, "{\"channel\":0}", 146, resp, sizeof resp) == 503);
    CHECK_STR_EQ("{\"error\":\"sensor read failed\"}", resp);
    board.adc_fail = 0;

    /* the input line: a limit switch, read as asserted or not, with the board's polarity applied */
    CHECK(ask(&ports, MM_PORTS_PATH_READ_PIN, "{\"pin\":12}", 147, resp, sizeof resp) == 200);
    CHECK_STR_EQ("{\"pin\":12,\"level\":false}", resp);              /* open: the line is high, the switch is active-low */
    board.gpio[12] = 0;                                                 /* the switch closes */
    CHECK(ask(&ports, MM_PORTS_PATH_READ_PIN, "{\"pin\":12}", 148, resp, sizeof resp) == 200);
    CHECK_STR_EQ("{\"pin\":12,\"level\":true}", resp);
    CHECK(ports.inputs[0].reads == 2);
    CHECK(ask(&ports, MM_PORTS_PATH_HELLO, "{}", 149, resp, sizeof resp) == 200);
    CHECK(strstr(resp, "\"inputs\":[{\"pin\":12,\"active_high\":false,\"level\":true}]") != NULL);
    CHECK(ask(&ports, MM_PORTS_PATH_READ_PIN, "{\"pin\":5}", 150, resp, sizeof resp) == 404);   /* an output is not an input */
    CHECK_STR_EQ("{\"error\":\"pin 5 is not an input of this board\"}", resp);
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":12,\"level\":true}", 150, resp, sizeof resp) == 404);   /* nor the reverse */
    CHECK(ask(&ports, MM_PORTS_PATH_READ_PIN, "{}", 150, resp, sizeof resp) == 400);
    CHECK_STR_EQ("{\"error\":\"read_pin needs 'pin'\"}", resp);
    board.gpio_fail_pin = 12;
    CHECK(ask(&ports, MM_PORTS_PATH_READ_PIN, "{\"pin\":12}", 151, resp, sizeof resp) == 503);   /* a fault, never a 'false' */
    CHECK_STR_EQ("{\"error\":\"the line could not be read\"}", resp);
    CHECK(ask(&ports, MM_PORTS_PATH_HELLO, "{}", 152, resp, sizeof resp) == 200);
    CHECK(strstr(resp, "\"inputs\":[{\"pin\":12,\"active_high\":false,\"level\":false}]") != NULL);   /* hello never invents a level */
    board.gpio_fail_pin = -1;
    board.gpio[12] = 1;                                                 /* the switch opens again */

    /* the watchdog: a Pi that goes quiet loses its outputs; the next request re-arms */
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":5,\"level\":true}", 200, resp, sizeof resp) == 200 && board.gpio[5] == 1);
    CHECK(mm_ports_service(&ports, 205) == 0 && board.gpio[5] == 1);
    CHECK(mm_ports_service(&ports, 229) == 0 && board.gpio[5] == 1);
    CHECK(mm_ports_service(&ports, 230) == 0 && board.gpio[5] == 0 && ports.watchdog_trips == 0);   /* max_on_s, not the watchdog */
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":6,\"level\":true}", 231, resp, sizeof resp) == 200 && board.gpio[6] == 0);
    CHECK(mm_ports_service(&ports, 291) == 0 && board.gpio[6] == 0 && ports.watchdog_trips == 0);   /* 60 s exactly: not yet */
    CHECK(mm_ports_service(&ports, 292) == 0 && board.gpio[6] == 1 && ports.watchdog_trips == 1 && ports.watchdog_fired);
    CHECK(mm_ports_service(&ports, 305) == 0 && ports.watchdog_trips == 1);   /* fires once per quiet period */
    CHECK(ask(&ports, MM_PORTS_PATH_HELLO, "{}", 310, resp, sizeof resp) == 200 && !ports.watchdog_fired);
    CHECK(strstr(resp, "\"tripped\":false") != NULL);
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":5,\"level\":true}", 311, resp, sizeof resp) == 200 && board.gpio[5] == 1);
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":5,\"level\":false}", 312, resp, sizeof resp) == 200 && board.gpio[5] == 0);

    /* a line that will not drive active: nothing happened, no hold */
    board.gpio_fail_pin = 5;
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":5,\"level\":true}", 400, resp, sizeof resp) == 503);
    CHECK_STR_EQ("{\"error\":\"the line would not drive active\"}", resp);
    CHECK(!ports.pins[0].active && !ports.tripped);
    board.gpio_fail_pin = -1;

    /* a line that will not RELEASE: the trip — nothing is driven active again until reboot */
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":5,\"level\":true}", 500, resp, sizeof resp) == 200);
    board.gpio_fail_pin = 5;
    CHECK(mm_ports_service(&ports, 530) == -1 && ports.tripped && ports.pins[0].release_failed && board.gpio[5] == 1);
    CHECK(ask(&ports, MM_PORTS_PATH_HELLO, "{}", 531, resp, sizeof resp) == 200);
    CHECK(strstr(resp, "\"tripped\":true") != NULL);
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":6,\"level\":true}", 532, resp, sizeof resp) == 409);
    CHECK_STR_EQ("{\"error\":\"tripped: a line would not release; nothing is driven active until reboot\"}", resp);
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":5,\"level\":false}", 533, resp, sizeof resp) == 503);
    CHECK_STR_EQ("{\"error\":\"the line would not release\"}", resp);
    board.gpio_fail_pin = -1;
    CHECK(mm_ports_service(&ports, 534) == -1 && board.gpio[5] == 0 && !ports.pins[0].active);   /* the retry releases it; the trip stands */
    CHECK(ask(&ports, MM_PORTS_PATH_READ, "{\"channel\":0}", 535, resp, sizeof resp) == 200);     /* reading still works */
    CHECK(ask(&ports, MM_PORTS_PATH_WRITE, "{\"pin\":6,\"level\":false}", 536, resp, sizeof resp) == 200);   /* driving SAFE is always allowed */

    compare_or_create();

    /* the frames around it */
    {
        uint8_t payload[256], frame[300], out[MM_PORTS_RESPONSE_CAP + 32];
        size_t pn, fn, on, i;
        mm_frame_decoder d;
        const uint8_t *body; size_t bl; int st;
        mm_frame_decoder_init(&d);

        pn = mm_frame_request_payload(MM_PORTS_PATH_READ, "{\"channel\":0}", 13, payload, sizeof payload);
        fn = mm_frame_encode(MM_FRAME_REQUEST, 42, payload, pn, frame, sizeof frame);
        for (i = 0; i < fn; i++) if (mm_frame_feed(&d, frame[i])) break;
        CHECK(i == fn - 1);
        on = mm_ports_on_frame(&ports, &d, 600, out, sizeof out);
        CHECK(on > 0);
        mm_frame_decoder_init(&d);
        for (i = 0; i < on; i++) if (mm_frame_feed(&d, out[i])) break;
        CHECK(d.type == MM_FRAME_RESPONSE && d.seq == 42);
        CHECK(mm_frame_parse_response(d.payload, d.payload_len, &st, &body, &bl) == 0 && st == 200);
        CHECK(bl == 26 && memcmp(body, "{\"channel\":0,\"volts\":0.75}", 26) == 0);

        /* a response frame is not ours to answer; a payload with no path line is a 400 */
        fn = mm_frame_encode(MM_FRAME_RESPONSE, 1, payload, pn, frame, sizeof frame);
        mm_frame_decoder_init(&d);
        for (i = 0; i < fn; i++) if (mm_frame_feed(&d, frame[i])) break;
        CHECK(mm_ports_on_frame(&ports, &d, 601, out, sizeof out) == 0);
        fn = mm_frame_encode(MM_FRAME_REQUEST, 2, (const uint8_t *)"nopath", 6, frame, sizeof frame);
        mm_frame_decoder_init(&d);
        for (i = 0; i < fn; i++) if (mm_frame_feed(&d, frame[i])) break;
        on = mm_ports_on_frame(&ports, &d, 602, out, sizeof out);
        mm_frame_decoder_init(&d);
        for (i = 0; i < on; i++) if (mm_frame_feed(&d, out[i])) break;
        CHECK(mm_frame_parse_response(d.payload, d.payload_len, &st, &body, &bl) == 0 && st == 400);
    }
}
