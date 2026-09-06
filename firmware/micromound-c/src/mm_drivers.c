#include "mm_drivers.h"
#include "mm_json.h"
#include "mm_time.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

/* ---- relay --------------------------------------------------------------------------------- */

static int drive(mm_relay *r, int active)
{
    return r->hal->gpio_write(r->hal->ctx, r->pin, active ? r->active_high : !r->active_high);
}

static int relay_run(void *ctx, const mm_execution *x, mm_outcome *out)
{
    mm_relay *r = (mm_relay *)ctx;
    double hold = 0;
    int found = 0;
    size_t i;

    /* on_s is required, so the kernel guarantees it is present; its absence is a fault, not a zero-length latch */
    for (i = 0; i < x->n_parameters; i++)
        if (strcmp(x->parameters[i].key, "on_s") == 0) { hold = x->parameters[i].value; found = 1; }
    if (!found) { out->succeeded = 0; strcpy(out->detail, "digital actuator requires an 'on_s' duration"); return 0; }

    /* already clamped to the intersected tiers; capped again here at the effective max_on_s as a last-resort belt */
    if (x->effective_limits.max_on_s.present && hold > x->effective_limits.max_on_s.value) hold = x->effective_limits.max_on_s.value;
    if (!(hold > 0) || !isfinite(hold)) {
        out->succeeded = 0;
        snprintf(out->detail, sizeof out->detail, "digital actuator needs a positive, bounded 'on_s', not %g", hold);
        return 0;
    }

    /* energize and HOLD; if the energize fails nothing was actuated — drive safe best-effort and fault */
    if (drive(r, 1) != 0) {
        drive(r, 0);
        r->has_hold = 0;
        out->succeeded = 0;
        strcpy(out->detail, "actuator could not drive its line active");
        return 0;
    }
    r->actuations++;
    r->has_hold = 1;
    r->held_until = x->started_at + (int64_t)ceil(hold);
    r->release_failed = 0;

    /* no evidence: a command is not evidence — a separate sensor confirms the effect, or the outcome stays unverified */
    out->succeeded = 1;
    out->has_ended_at = 0;
    out->n_evidence = 0;
    return 0;
}

int mm_relay_init(mm_relay *r, const mm_hal *hal, const char *capability, int pin, int active_high)
{
    memset(r, 0, sizeof *r);
    r->hal = hal;
    r->capability = capability;
    r->pin = pin;
    r->active_high = active_high;
    r->executor.capability_id = capability;
    r->executor.run = relay_run;
    r->executor.ctx = r;
    r->executor.available = 1;
    return drive(r, 0);   /* start at the SAFE level */
}

int mm_relay_service(mm_relay *r, int64_t now)
{
    if (!r->has_hold || now < r->held_until) return 0;
    if (drive(r, 0) != 0) { r->release_failed = 1; return -1; }   /* the hold stays pending; the next tick retries */
    r->has_hold = 0;
    r->release_failed = 0;
    return 0;
}

int mm_relay_safe(mm_relay *r)
{
    if (drive(r, 0) != 0) { r->release_failed = 1; return -1; }
    r->has_hold = 0;
    r->release_failed = 0;
    return 0;
}

/* ---- probe --------------------------------------------------------------------------------- */

size_t mm_reading_payload(double value, const char *unit, const char *capability, char *out, size_t cap)
{
    mm_json w;
    mm_json_init(&w, out, cap);
    mm_json_object_begin(&w);
    mm_json_kv_double(&w, "value", value);
    mm_json_kv_string(&w, "unit", unit ? unit : "");
    mm_json_kv_string(&w, "capability", capability);
    mm_json_object_end(&w);
    return mm_json_finish(&w);
}

static int probe_run(void *ctx, const mm_execution *x, mm_outcome *out)
{
    mm_probe *p = (mm_probe *)ctx;
    double volts, value;
    mm_evidence_produced *item;

    if (p->hal->adc_read(p->hal->ctx, p->channel, &volts) != 0 || !isfinite(volts)) {
        out->succeeded = 0;
        strcpy(out->detail, "sensor read failed");   /* a fault with NO reading, never a zero */
        return 0;
    }
    value = volts * p->scale + p->offset;
    if (!isfinite(value)) { out->succeeded = 0; strcpy(out->detail, "sensor read failed"); return 0; }

    p->n++;
    item = &out->evidence[0];
    snprintf(item->id, sizeof item->id, "e-%s-%d", p->capability, p->n);
    mm_time_format(x->started_at, item->captured_at, sizeof item->captured_at);
    strcpy(item->type, "reading");
    snprintf(item->source, sizeof item->source, "%s", p->capability);
    if (mm_reading_payload(value, p->unit, p->capability, item->payload_json, sizeof item->payload_json) == 0) {
        out->succeeded = 0;
        strcpy(out->detail, "sensor reading does not fit its payload");
        return 0;
    }
    out->n_evidence = 1;
    out->succeeded = 1;
    out->has_ended_at = 1;
    out->ended_at = x->started_at;
    return 0;
}

void mm_probe_init(mm_probe *p, const mm_hal *hal, const char *capability, int channel, double scale, double offset, const char *unit)
{
    memset(p, 0, sizeof *p);
    p->hal = hal;
    p->capability = capability;
    p->channel = channel;
    p->scale = scale;
    p->offset = offset;
    p->unit = unit ? unit : "";
    p->executor.capability_id = capability;
    p->executor.run = probe_run;
    p->executor.ctx = p;
    p->executor.available = 1;
}

/* ---- switch -------------------------------------------------------------------------------- */

static int switch_run(void *ctx, const mm_execution *x, mm_outcome *out)
{
    mm_switch *s = (mm_switch *)ctx;
    int level = 0;
    mm_evidence_produced *item;

    if (s->hal->gpio_read(s->hal->ctx, s->pin, &level) != 0) {
        out->succeeded = 0;
        strcpy(out->detail, "input read failed");   /* a fault with NO reading, never a 0 */
        return 0;
    }

    s->n++;
    item = &out->evidence[0];
    snprintf(item->id, sizeof item->id, "e-%s-%d", s->capability, s->n);
    mm_time_format(x->started_at, item->captured_at, sizeof item->captured_at);
    strcpy(item->type, "reading");
    snprintf(item->source, sizeof item->source, "%s", s->capability);
    if (mm_reading_payload((level != 0) == (s->active_high != 0) ? 1 : 0, s->unit, s->capability,
                           item->payload_json, sizeof item->payload_json) == 0) {
        out->succeeded = 0;
        strcpy(out->detail, "switch reading does not fit its payload");
        return 0;
    }
    out->n_evidence = 1;
    out->succeeded = 1;
    out->has_ended_at = 1;
    out->ended_at = x->started_at;
    return 0;
}

void mm_switch_init(mm_switch *s, const mm_hal *hal, const char *capability, int pin, int active_high, const char *unit)
{
    memset(s, 0, sizeof *s);
    s->hal = hal;
    s->capability = capability;
    s->pin = pin;
    s->active_high = active_high ? 1 : 0;
    s->unit = unit ? unit : "";
    s->executor.capability_id = capability;
    s->executor.run = switch_run;
    s->executor.ctx = s;
    s->executor.available = 1;
}
