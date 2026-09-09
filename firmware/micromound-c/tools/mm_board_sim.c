/*
 * mm_board_sim — the real port-server firmware (mm_ports, mm_frame) on a host, over stdin/stdout.
 *
 * The acceptance harness (src/Micromound.Acceptance) drives a whole mound against THIS, so that the
 * Pi-class runtime's generic drivers, the link framing, and the board's own answers are the ones
 * that ship — not a C# imitation of them. The only thing simulated is the world below the HAL: a
 * few lines, a few channels, and enough physics that an actuation has an observable consequence a
 * sensor can independently confirm.
 *
 * Frames are the §12 framing on stdin/stdout; logs go to stderr. Everything the board answers comes
 * from mm_ports_handle. Three paths are the SIMULATOR'S OWN, handled before the board sees them —
 * a real board 404s them, which is the correct answer and is what makes them safe to add:
 *
 *   micromound/link/sim/advance {"seconds":N}  advance the simulated clock N seconds, one second at
 *                                             a time (mm_ports_service each second, physics each
 *                                             second), then answer with the world
 *   micromound/link/sim/world   {}             the world as it stands
 *   micromound/link/sim/fault   {"gpio_fail_pin":N,"adc_fail":true,"read_fail_pin":N}
 *                                             inject a failure; -1 / false clears one
 *
 * The clock only moves on `advance`, so a whole acceptance run is deterministic: no wall clock, no
 * sleeps, no flakes.
 *
 *   mm_board_sim [--profile S] [--firmware S] [--watchdog N]
 *                [--pin PIN[:low][:MAX_ON_S]]            an output line (default active-high, no bound)
 *                [--input PIN[:low][:follows=PIN][:delay=S]]
 *                                                        an input line, optionally asserted while an
 *                                                        output is active, after a travel delay
 *                [--channel CH[:VOLTS][:rises=PIN@RATE]]  an analog channel, optionally rising while
 *                                                        an output is active (volts per second)
 */
#include "mm_frame.h"
#include "mm_json.h"
#include "mm_json_read.h"
#include "mm_ports.h"
#include "mm_version.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MAX_LINES 64
#define SIM_PATH_ADVANCE "micromound/link/sim/advance"
#define SIM_PATH_WORLD "micromound/link/sim/world"
#define SIM_PATH_FAULT "micromound/link/sim/fault"

typedef struct sim_input {
    int pin;
    int follows;                /* an output pin this input is asserted by; -1 = free */
    int delay_s;                /* travel time before it asserts */
    int64_t active_since;       /* when `follows` went active; -1 = not active */
} sim_input;

typedef struct sim_channel {
    int channel;
    double volts;
    int rises_with;             /* an output pin; -1 = still */
    double rate;                /* volts per simulated second while that pin is active */
} sim_channel;

typedef struct world {
    int level[MAX_LINES];       /* physical levels */
    int gpio_fail_pin;
    int read_fail_pin;
    int adc_fail;
    int64_t now;

    sim_input inputs[MM_PORTS_MAX_INPUTS];
    size_t n_inputs;
    sim_channel channels[MM_PORTS_MAX_CHANNELS];
    size_t n_channels;
} world;

static world w;
static mm_ports ports;

/* ---- the HAL below the board -------------------------------------------------------------- */

static int64_t sim_now(void *ctx) { (void)ctx; return w.now; }
static int sim_random(void *ctx, uint8_t *out, size_t n) { (void)ctx; memset(out, 0, n); return 0; }

static int sim_gpio_write(void *ctx, int pin, int level)
{
    (void)ctx;
    if (pin < 0 || pin >= MAX_LINES || pin == w.gpio_fail_pin) return -1;
    w.level[pin] = level ? 1 : 0;
    return 0;
}

static int sim_gpio_read(void *ctx, int pin, int *level)
{
    (void)ctx;
    if (pin < 0 || pin >= MAX_LINES || pin == w.read_fail_pin) return -1;
    *level = w.level[pin];
    return 0;
}

static int sim_adc_read(void *ctx, int channel, double *volts)
{
    size_t i;
    (void)ctx;
    if (w.adc_fail) return -1;
    for (i = 0; i < w.n_channels; i++)
        if (w.channels[i].channel == channel) { *volts = w.channels[i].volts; return 0; }
    return -1;
}

/* monotonic_s is NULL: a port server keeps no operating budgets, so it has nothing to age. */
static const mm_hal HAL = { NULL, sim_now, sim_random, NULL, NULL, NULL, sim_gpio_write, sim_gpio_read, sim_adc_read, NULL };

/* ---- the physics ---------------------------------------------------------------------------- */

/* Is output pin `pin` driven active right now (its logical level, through the board's polarity)? */
static int output_active(int pin)
{
    size_t i;
    for (i = 0; i < ports.n_pins; i++)
        if (ports.pins[i].pin == pin) return ports.pins[i].active;
    return 0;
}

/* One simulated second: what an actuation does to the world the sensors see. */
static void physics_tick(void)
{
    size_t i;
    for (i = 0; i < w.n_channels; i++) {
        sim_channel *c = &w.channels[i];
        if (c->rises_with >= 0 && output_active(c->rises_with)) c->volts += c->rate;
    }
    for (i = 0; i < w.n_inputs; i++) {
        sim_input *in = &w.inputs[i];
        int asserted;
        size_t j;
        int active_high = 1;
        if (in->follows < 0) continue;                       /* a free line: whoever set it owns it */
        if (!output_active(in->follows)) { in->active_since = -1; asserted = 0; }
        else {
            if (in->active_since < 0) in->active_since = w.now;
            asserted = w.now - in->active_since >= in->delay_s;   /* mechanical travel */
        }
        for (j = 0; j < ports.n_inputs; j++)
            if (ports.inputs[j].pin == in->pin) active_high = ports.inputs[j].active_high;
        w.level[in->pin] = asserted ? (active_high ? 1 : 0) : (active_high ? 0 : 1);
    }
}

/* ---- the simulator's own answers ------------------------------------------------------------ */

static size_t world_body(char *out, size_t cap)
{
    mm_json j;
    size_t i;
    mm_json_init(&j, out, cap);
    mm_json_object_begin(&j);
    mm_json_kv_int(&j, "now", w.now);
    mm_json_kv_bool(&j, "tripped", ports.tripped);
    mm_json_kv_int(&j, "requests", (long long)ports.requests);
    mm_json_kv_int(&j, "watchdog_trips", (long long)ports.watchdog_trips);
    mm_json_key(&j, "pins");
    mm_json_array_begin(&j);
    for (i = 0; i < ports.n_pins; i++) {
        mm_json_object_begin(&j);
        mm_json_kv_int(&j, "pin", ports.pins[i].pin);
        mm_json_kv_bool(&j, "active", ports.pins[i].active);
        mm_json_kv_bool(&j, "level", w.level[ports.pins[i].pin]);
        mm_json_kv_int(&j, "writes", (long long)ports.pins[i].writes);
        mm_json_kv_int(&j, "auto_releases", (long long)ports.pins[i].auto_releases);
        mm_json_object_end(&j);
    }
    mm_json_array_end(&j);
    mm_json_key(&j, "inputs");
    mm_json_array_begin(&j);
    for (i = 0; i < ports.n_inputs; i++) {
        mm_json_object_begin(&j);
        mm_json_kv_int(&j, "pin", ports.inputs[i].pin);
        mm_json_kv_bool(&j, "level", w.level[ports.inputs[i].pin]);
        mm_json_kv_int(&j, "reads", (long long)ports.inputs[i].reads);
        mm_json_object_end(&j);
    }
    mm_json_array_end(&j);
    mm_json_key(&j, "channels");
    mm_json_array_begin(&j);
    for (i = 0; i < w.n_channels; i++) {
        mm_json_object_begin(&j);
        mm_json_kv_int(&j, "channel", w.channels[i].channel);
        mm_json_kv_double(&j, "volts", w.channels[i].volts);
        mm_json_object_end(&j);
    }
    mm_json_array_end(&j);
    mm_json_object_end(&j);
    return mm_json_finish(&j);
}

/* Reads {"seconds":N} / {"gpio_fail_pin":N,"read_fail_pin":N,"adc_fail":bool}. Returns 0 or -1. */
static int sim_request(const char *body, size_t n, long long *seconds, int *set_fault)
{
    mm_jr r;
    char key[32];
    int more;
    size_t i;
    int blank = 1;

    for (i = 0; i < n; i++) if (body[i] != ' ' && body[i] != '\t' && body[i] != '\n' && body[i] != '\r') { blank = 0; break; }
    if (blank) return 0;
    mm_jr_init(&r, body, n);
    if (mm_jr_object_begin(&r) != 0) return -1;
    while ((more = mm_jr_object_next(&r, key, sizeof key)) == 1) {
        long long v;
        int b;
        if (strcmp(key, "seconds") == 0) { if (mm_jr_int(&r, &v) != 0) return -1; *seconds = v; }
        else if (strcmp(key, "gpio_fail_pin") == 0) { if (mm_jr_int(&r, &v) != 0) return -1; w.gpio_fail_pin = (int)v; *set_fault = 1; }
        else if (strcmp(key, "read_fail_pin") == 0) { if (mm_jr_int(&r, &v) != 0) return -1; w.read_fail_pin = (int)v; *set_fault = 1; }
        else if (strcmp(key, "adc_fail") == 0) { if (mm_jr_bool(&r, &b) != 0) return -1; w.adc_fail = b; *set_fault = 1; }
        else if (mm_jr_skip(&r) != 0) return -1;
    }
    if (more != 0 || mm_jr_end(&r) != 0) return -1;
    return 0;
}

/* Handles a simulator path. Returns the status, or -1 when the path is not the simulator's. */
static int sim_handle(const char *path, size_t path_len, const char *body, size_t body_len, char *resp, size_t cap, size_t *resp_len)
{
    long long seconds = 0;
    int set_fault = 0;
    int is_advance = path_len == sizeof(SIM_PATH_ADVANCE) - 1 && memcmp(path, SIM_PATH_ADVANCE, path_len) == 0;
    int is_world = path_len == sizeof(SIM_PATH_WORLD) - 1 && memcmp(path, SIM_PATH_WORLD, path_len) == 0;
    int is_fault = path_len == sizeof(SIM_PATH_FAULT) - 1 && memcmp(path, SIM_PATH_FAULT, path_len) == 0;

    if (!is_advance && !is_world && !is_fault) return -1;
    if (sim_request(body, body_len, &seconds, &set_fault) != 0) {
        *resp_len = (size_t)snprintf(resp, cap, "{\"error\":\"simulator request unreadable\"}");
        return 400;
    }
    if (is_advance) {
        long long i;
        if (seconds < 0) seconds = 0;
        if (seconds > 100000) seconds = 100000;              /* a bounded simulation, never a hang */
        for (i = 0; i < seconds; i++) {
            w.now++;
            physics_tick();
            mm_ports_service(&ports, w.now);
            physics_tick();                                  /* the board may have released a line this second */
        }
    }
    *resp_len = world_body(resp, cap);
    return 200;
}

/* ---- argv ----------------------------------------------------------------------------------- */

/* Splits "5:low:30" into up to 4 fields at ':'. Returns the count. Modifies the string. */
static int split(char *text, char *fields[], int max)
{
    int n = 0;
    char *p = text;
    fields[n++] = p;
    while (*p && n < max) {
        if (*p == ':') { *p = '\0'; fields[n++] = p + 1; }
        p++;
    }
    return n;
}

static const char *value_of(const char *field, const char *key)
{
    size_t k = strlen(key);
    if (strncmp(field, key, k) == 0 && field[k] == '=') return field + k + 1;
    return NULL;
}

static void usage(void)
{
    fprintf(stderr,
        "mm_board_sim — the port-server firmware on a host, over stdin/stdout (PROTOCOL.md §12)\n"
        "  --profile S --firmware S --watchdog N\n"
        "  --pin PIN[:low][:MAX_ON_S]\n"
        "  --input PIN[:low][:follows=PIN][:delay=S]\n"
        "  --channel CH[:VOLTS][:rises=PIN@RATE]\n");
}

int main(int argc, char **argv)
{
    const char *profile = "bench";
    const char *firmware = MM_VERSION;
    long watchdog = 5;
    int i;
    mm_frame_decoder decoder;
    uint8_t out[MM_PORTS_RESPONSE_CAP + 64];
    int byte;

    memset(&w, 0, sizeof w);
    w.gpio_fail_pin = -1;
    w.read_fail_pin = -1;

    /* the tables first, so mm_ports_init sees the world it is about to drive safe */
    for (i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--profile") == 0 && i + 1 < argc) profile = argv[++i];
        else if (strcmp(argv[i], "--firmware") == 0 && i + 1 < argc) firmware = argv[++i];
        else if (strcmp(argv[i], "--watchdog") == 0 && i + 1 < argc) watchdog = strtol(argv[++i], NULL, 10);
        else if (strcmp(argv[i], "--help") == 0) { usage(); return 0; }
    }
    mm_ports_init(&ports, &HAL, profile, firmware, watchdog);

    for (i = 1; i < argc; i++) {
        char *fields[6];
        int n, f;
        if (strcmp(argv[i], "--pin") == 0 && i + 1 < argc) {
            int pin, active_high = 1;
            double max_on = 0;
            n = split(argv[++i], fields, 6);
            pin = (int)strtol(fields[0], NULL, 10);
            for (f = 1; f < n; f++) {
                if (strcmp(fields[f], "low") == 0) active_high = 0;
                else if (strcmp(fields[f], "high") == 0) active_high = 1;
                else max_on = strtod(fields[f], NULL);
            }
            if (mm_ports_add_pin(&ports, pin, active_high, max_on) != 0) { fprintf(stderr, "sim: pin %d refused\n", pin); return 2; }
        } else if (strcmp(argv[i], "--input") == 0 && i + 1 < argc) {
            int pin, active_high = 1, follows = -1, delay = 0;
            const char *v;
            n = split(argv[++i], fields, 6);
            pin = (int)strtol(fields[0], NULL, 10);
            for (f = 1; f < n; f++) {
                if (strcmp(fields[f], "low") == 0) active_high = 0;
                else if (strcmp(fields[f], "high") == 0) active_high = 1;
                else if ((v = value_of(fields[f], "follows")) != NULL) follows = (int)strtol(v, NULL, 10);
                else if ((v = value_of(fields[f], "delay")) != NULL) delay = (int)strtol(v, NULL, 10);
            }
            if (pin < 0 || pin >= MAX_LINES) { fprintf(stderr, "sim: input %d out of range\n", pin); return 2; }
            /* an input that follows an output starts NOT asserted, whatever its polarity */
            w.level[pin] = active_high ? 0 : 1;
            if (mm_ports_add_input(&ports, pin, active_high) != 0) { fprintf(stderr, "sim: input %d refused\n", pin); return 2; }
            if (w.n_inputs < MM_PORTS_MAX_INPUTS) {
                w.inputs[w.n_inputs].pin = pin;
                w.inputs[w.n_inputs].follows = follows;
                w.inputs[w.n_inputs].delay_s = delay;
                w.inputs[w.n_inputs].active_since = -1;
                w.n_inputs++;
            }
        } else if (strcmp(argv[i], "--channel") == 0 && i + 1 < argc) {
            int channel, rises = -1;
            double volts = 0, rate = 0;
            const char *v;
            n = split(argv[++i], fields, 6);
            channel = (int)strtol(fields[0], NULL, 10);
            for (f = 1; f < n; f++) {
                if ((v = value_of(fields[f], "rises")) != NULL) {
                    const char *at = strchr(v, '@');
                    rises = (int)strtol(v, NULL, 10);
                    rate = at ? strtod(at + 1, NULL) : 0;
                } else volts = strtod(fields[f], NULL);
            }
            if (w.n_channels < MM_PORTS_MAX_CHANNELS) {
                w.channels[w.n_channels].channel = channel;
                w.channels[w.n_channels].volts = volts;
                w.channels[w.n_channels].rises_with = rises;
                w.channels[w.n_channels].rate = rate;
                w.n_channels++;
            }
            if (mm_ports_add_channel(&ports, channel) != 0) { fprintf(stderr, "sim: channel %d refused\n", channel); return 2; }
        }
    }

    fprintf(stderr, "sim: %s firmware %s — %u pin(s), %u input(s), %u channel(s), watchdog %lds\n",
            profile, firmware, (unsigned)ports.n_pins, (unsigned)ports.n_inputs, (unsigned)ports.n_channels, watchdog);
    fflush(stderr);

    mm_frame_decoder_init(&decoder);
    while ((byte = fgetc(stdin)) != EOF) {
        size_t n;
        if (!mm_frame_feed(&decoder, (uint8_t)byte)) continue;
        if (decoder.type != MM_FRAME_REQUEST) continue;
        {
            const uint8_t *path, *body;
            size_t path_len, body_len;
            char resp[MM_PORTS_RESPONSE_CAP];
            size_t resp_len = 0;
            uint8_t payload[MM_PORTS_RESPONSE_CAP + 8];
            int status = -1;

            if (mm_frame_parse_request(decoder.payload, decoder.payload_len, &path, &path_len, &body, &body_len) == 0)
                status = sim_handle((const char *)path, path_len, (const char *)body, body_len, resp, sizeof resp, &resp_len);

            if (status >= 0) {                                   /* the simulator's own path */
                n = mm_frame_response_payload(status, resp, resp_len, payload, sizeof payload);
                n = n ? mm_frame_encode(MM_FRAME_RESPONSE, decoder.seq, payload, n, out, sizeof out) : 0;
            } else {                                             /* everything else is the real board's */
                n = mm_ports_on_frame(&ports, &decoder, w.now, out, sizeof out);
                physics_tick();                                  /* a write has consequences the same instant */
            }
            if (n) {
                fwrite(out, 1, n, stdout);
                fflush(stdout);
            }
        }
    }
    return 0;
}
