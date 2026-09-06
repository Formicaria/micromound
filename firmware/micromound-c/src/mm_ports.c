#include "mm_ports.h"
#include "mm_json.h"
#include "mm_json_read.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static int drive(mm_ports *p, mm_port_pin *pin, int active)
{
    return p->hal->gpio_write(p->hal->ctx, pin->pin, active ? pin->active_high : !pin->active_high);
}

void mm_ports_init(mm_ports *p, const mm_hal *hal, const char *profile, const char *firmware, int64_t watchdog_s)
{
    memset(p, 0, sizeof *p);
    p->hal = hal;
    p->profile = profile ? profile : "";
    p->firmware = firmware ? firmware : "";
    p->watchdog_s = watchdog_s > 0 ? watchdog_s : 0;
}

static mm_port_pin *find_pin(mm_ports *p, int pin)
{
    size_t i;
    for (i = 0; i < p->n_pins; i++) if (p->pins[i].pin == pin) return &p->pins[i];
    return NULL;
}

static mm_port_input *find_input(mm_ports *p, int pin)
{
    size_t i;
    for (i = 0; i < p->n_inputs; i++) if (p->inputs[i].pin == pin) return &p->inputs[i];
    return NULL;
}

static int has_channel(const mm_ports *p, int channel)
{
    size_t i;
    for (i = 0; i < p->n_channels; i++) if (p->channels[i] == channel) return 1;
    return 0;
}

int mm_ports_add_pin(mm_ports *p, int pin, int active_high, double max_on_s)
{
    mm_port_pin *slot;
    if (p->n_pins >= MM_PORTS_MAX_PINS || find_pin(p, pin) || find_input(p, pin)) return -1;
    slot = &p->pins[p->n_pins];
    memset(slot, 0, sizeof *slot);
    slot->pin = pin;
    slot->active_high = active_high ? 1 : 0;
    slot->max_on_s = (max_on_s > 0 && isfinite(max_on_s)) ? max_on_s : 0;
    if (drive(p, slot, 0) != 0) return -1;              /* the line comes up at its SAFE level or the pin is not offered */
    p->n_pins++;
    return 0;
}

int mm_ports_add_input(mm_ports *p, int pin, int active_high)
{
    int level = 0;
    if (p->n_inputs >= MM_PORTS_MAX_INPUTS || find_input(p, pin) || find_pin(p, pin)) return -1;
    /* a line the board cannot read at bring-up is not offered: better an absent port than a phantom switch */
    if (p->hal->gpio_read(p->hal->ctx, pin, &level) != 0) return -1;
    p->inputs[p->n_inputs].pin = pin;
    p->inputs[p->n_inputs].active_high = active_high ? 1 : 0;
    p->inputs[p->n_inputs].reads = 0;
    p->n_inputs++;
    return 0;
}

int mm_ports_add_channel(mm_ports *p, int channel)
{
    if (p->n_channels >= MM_PORTS_MAX_CHANNELS || has_channel(p, channel)) return -1;
    p->channels[p->n_channels++] = channel;
    return 0;
}

int mm_ports_safe(mm_ports *p)
{
    size_t i;
    int failed = 0;
    for (i = 0; i < p->n_pins; i++) {
        mm_port_pin *pin = &p->pins[i];
        if (drive(p, pin, 0) != 0) { pin->release_failed = 1; failed = 1; continue; }
        pin->active = 0;
        pin->held_until = 0;
        pin->release_failed = 0;
    }
    if (failed) p->tripped = 1;
    return failed ? -1 : 0;
}

int mm_ports_service(mm_ports *p, int64_t now)
{
    size_t i;
    for (i = 0; i < p->n_pins; i++) {
        mm_port_pin *pin = &p->pins[i];
        if (!pin->active || pin->held_until == 0 || now < pin->held_until) continue;
        if (drive(p, pin, 0) != 0) { pin->release_failed = 1; p->tripped = 1; continue; }   /* retried next tick; trip stands */
        pin->active = 0;
        pin->held_until = 0;
        pin->auto_releases++;
    }
    if (p->watchdog_s > 0 && p->last_request_at > 0 && !p->watchdog_fired && now - p->last_request_at > p->watchdog_s) {
        p->watchdog_fired = 1;
        p->watchdog_trips++;
        mm_ports_safe(p);
    }
    return p->tripped ? -1 : 0;
}

/* ---- request bodies ------------------------------------------------------------------------- */

/* Reads a flat object of the members the port requests use. Returns 0, or -1 when the body is not such an object. */
static int read_request(const char *body, size_t n, int *pin, int *has_pin, int *level, int *has_level, int *channel, int *has_channel_)
{
    mm_jr r;
    char key[32];
    int more;
    size_t i;
    int blank = 1;

    *has_pin = *has_level = *has_channel_ = 0;
    for (i = 0; i < n; i++) if (body[i] != ' ' && body[i] != '\t' && body[i] != '\n' && body[i] != '\r') { blank = 0; break; }
    if (blank) return 0;                                 /* an empty body reads as {} */

    mm_jr_init(&r, body, n);
    if (mm_jr_object_begin(&r) != 0) return -1;
    while ((more = mm_jr_object_next(&r, key, sizeof key)) == 1) {
        long long v;
        int b;
        if (strcmp(key, "pin") == 0) { if (mm_jr_int(&r, &v) != 0) return -1; *pin = (int)v; *has_pin = 1; }
        else if (strcmp(key, "channel") == 0) { if (mm_jr_int(&r, &v) != 0) return -1; *channel = (int)v; *has_channel_ = 1; }
        else if (strcmp(key, "level") == 0) { if (mm_jr_bool(&r, &b) != 0) return -1; *level = b; *has_level = 1; }
        else if (mm_jr_skip(&r) != 0) return -1;
    }
    if (more != 0 || mm_jr_end(&r) != 0) return -1;
    return 0;
}

static size_t error_body(const char *message, char *resp, size_t cap)
{
    mm_json w;
    mm_json_init(&w, resp, cap);
    mm_json_object_begin(&w);
    mm_json_kv_string(&w, "error", message);
    mm_json_object_end(&w);
    return mm_json_finish(&w);
}

static int respond(int status, size_t n, size_t *resp_len)
{
    *resp_len = n;
    return status;
}

int mm_ports_handle(mm_ports *p, const char *path, size_t path_len, const char *body, size_t body_len,
                    int64_t now, char *resp, size_t cap, size_t *resp_len)
{
    int pin = 0, has_pin, level = 0, has_level, channel = 0, has_channel_;
    mm_json w;
    size_t i;

    p->requests++;
    p->last_request_at = now;
    p->watchdog_fired = 0;                                /* the link is talking again */
    if (cap) resp[0] = '\0';
    *resp_len = 0;

    if (read_request(body, body_len, &pin, &has_pin, &level, &has_level, &channel, &has_channel_) != 0) {
        p->rejected++;
        return respond(400, error_body("request body is not a JSON object of pin/level/channel", resp, cap), resp_len);
    }

#define IS_PATH(lit) (path_len == sizeof(lit) - 1 && memcmp(path, lit, path_len) == 0)
    if (IS_PATH(MM_PORTS_PATH_HELLO)) {
        mm_json_init(&w, resp, cap);
        mm_json_object_begin(&w);
        mm_json_kv_string(&w, "profile", p->profile);
        mm_json_kv_string(&w, "firmware", p->firmware);
        mm_json_kv_int(&w, "watchdog_s", (long long)p->watchdog_s);
        mm_json_kv_bool(&w, "tripped", p->tripped);
        mm_json_key(&w, "pins");
        mm_json_array_begin(&w);
        for (i = 0; i < p->n_pins; i++) {
            mm_json_object_begin(&w);
            mm_json_kv_int(&w, "pin", p->pins[i].pin);
            mm_json_kv_bool(&w, "active_high", p->pins[i].active_high);
            mm_json_kv_double(&w, "max_on_s", p->pins[i].max_on_s);
            mm_json_kv_bool(&w, "level", p->pins[i].active);
            mm_json_object_end(&w);
        }
        mm_json_array_end(&w);
        mm_json_key(&w, "inputs");
        mm_json_array_begin(&w);
        for (i = 0; i < p->n_inputs; i++) {
            int lvl = 0;
            int ok = p->hal->gpio_read(p->hal->ctx, p->inputs[i].pin, &lvl) == 0;
            mm_json_object_begin(&w);
            mm_json_kv_int(&w, "pin", p->inputs[i].pin);
            mm_json_kv_bool(&w, "active_high", p->inputs[i].active_high);
            /* a line that will not read reports false and says so on its own read; hello never invents a level */
            mm_json_kv_bool(&w, "level", ok && (lvl != 0) == (p->inputs[i].active_high != 0));
            mm_json_object_end(&w);
        }
        mm_json_array_end(&w);
        mm_json_key(&w, "channels");
        mm_json_array_begin(&w);
        for (i = 0; i < p->n_channels; i++) mm_json_int(&w, p->channels[i]);
        mm_json_array_end(&w);
        mm_json_object_end(&w);
        return respond(200, mm_json_finish(&w), resp_len);
    }

    if (IS_PATH(MM_PORTS_PATH_WRITE)) {
        mm_port_pin *target;
        char msg[96];
        if (!has_pin || !has_level) { p->rejected++; return respond(400, error_body("write needs 'pin' and 'level'", resp, cap), resp_len); }
        target = find_pin(p, pin);
        if (!target) {
            p->rejected++;
            snprintf(msg, sizeof msg, "pin %d is not a port of this board", pin);
            return respond(404, error_body(msg, resp, cap), resp_len);
        }
        if (level && p->tripped) {
            p->rejected++;
            return respond(409, error_body("tripped: a line would not release; nothing is driven active until reboot", resp, cap), resp_len);
        }
        if (drive(p, target, level) != 0) {
            p->rejected++;
            if (!level) { target->release_failed = 1; p->tripped = 1; }
            else drive(p, target, 0);
            return respond(503, error_body(level ? "the line would not drive active" : "the line would not release", resp, cap), resp_len);
        }
        target->writes++;
        target->active = level ? 1 : 0;
        target->held_until = (level && target->max_on_s > 0) ? now + (int64_t)ceil(target->max_on_s) : 0;
        target->release_failed = 0;
        mm_json_init(&w, resp, cap);
        mm_json_object_begin(&w);
        mm_json_kv_int(&w, "pin", pin);
        mm_json_kv_bool(&w, "level", target->active);
        mm_json_object_end(&w);
        return respond(200, mm_json_finish(&w), resp_len);
    }

    if (IS_PATH(MM_PORTS_PATH_READ_PIN)) {
        mm_port_input *in;
        char msg[96];
        int level = 0;
        if (!has_pin) { p->rejected++; return respond(400, error_body("read_pin needs 'pin'", resp, cap), resp_len); }
        in = find_input(p, pin);
        if (!in) {
            p->rejected++;
            snprintf(msg, sizeof msg, "pin %d is not an input of this board", pin);
            return respond(404, error_body(msg, resp, cap), resp_len);
        }
        if (p->hal->gpio_read(p->hal->ctx, pin, &level) != 0) {
            p->rejected++;
            return respond(503, error_body("the line could not be read", resp, cap), resp_len);   /* a fault, never a 'false' */
        }
        in->reads++;
        mm_json_init(&w, resp, cap);
        mm_json_object_begin(&w);
        mm_json_kv_int(&w, "pin", pin);
        mm_json_kv_bool(&w, "level", (level != 0) == (in->active_high != 0));
        mm_json_object_end(&w);
        return respond(200, mm_json_finish(&w), resp_len);
    }

    if (IS_PATH(MM_PORTS_PATH_READ)) {
        double volts = 0;
        char msg[96];
        if (!has_channel_) { p->rejected++; return respond(400, error_body("read needs 'channel'", resp, cap), resp_len); }
        if (!has_channel(p, channel)) {
            p->rejected++;
            snprintf(msg, sizeof msg, "channel %d is not a port of this board", channel);
            return respond(404, error_body(msg, resp, cap), resp_len);
        }
        if (p->hal->adc_read(p->hal->ctx, channel, &volts) != 0 || !isfinite(volts)) {
            p->rejected++;
            return respond(503, error_body("sensor read failed", resp, cap), resp_len);   /* a fault, never a zero */
        }
        mm_json_init(&w, resp, cap);
        mm_json_object_begin(&w);
        mm_json_kv_int(&w, "channel", channel);
        mm_json_kv_double(&w, "volts", volts);
        mm_json_object_end(&w);
        return respond(200, mm_json_finish(&w), resp_len);
    }
#undef IS_PATH

    p->rejected++;
    return respond(404, error_body("not a port request", resp, cap), resp_len);
}

size_t mm_ports_on_frame(mm_ports *p, const mm_frame_decoder *d, int64_t now, uint8_t *out, size_t cap)
{
    const uint8_t *path, *body;
    size_t path_len, body_len, n, rn;
    char resp[MM_PORTS_RESPONSE_CAP];
    uint8_t payload[MM_PORTS_RESPONSE_CAP + 8];
    int status;

    if (d->type != MM_FRAME_REQUEST) return 0;
    if (mm_frame_parse_request(d->payload, d->payload_len, &path, &path_len, &body, &body_len) != 0) {
        p->requests++;
        p->rejected++;
        status = 400;
        rn = error_body("malformed request payload", resp, sizeof resp);
    } else {
        status = mm_ports_handle(p, (const char *)path, path_len, (const char *)body, body_len, now, resp, sizeof resp, &rn);
    }
    n = mm_frame_response_payload(status, resp, rn, payload, sizeof payload);
    if (n == 0) return 0;
    return mm_frame_encode(MM_FRAME_RESPONSE, d->seq, payload, n, out, cap);
}
