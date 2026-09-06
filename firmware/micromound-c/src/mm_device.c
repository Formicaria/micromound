#include "mm_device.h"
#include "mm_envelope.h"
#include "mm_json_read.h"
#include "mm_time.h"

#include <stdio.h>
#include <string.h>

/* ---- helpers ------------------------------------------------------------------------------ */

static void set_str(char *dst, size_t cap, const char *src)
{
    size_t n = strlen(src);
    if (n >= cap) n = cap - 1;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

static void audit(mm_device *d, const char *line)
{
    if (d->audit_count == MM_DEVICE_AUDIT) {
        memmove(d->audit[0], d->audit[1], (MM_DEVICE_AUDIT - 1) * MM_REASON_CAP);
        d->audit_count--;
    }
    set_str(d->audit[d->audit_count++], MM_REASON_CAP, line);
}

static mm_queued *queue_slot(mm_device *d, size_t i)
{
    return &d->queue[(d->queue_head + i) % MM_DEVICE_QUEUE];
}

/* ---- init ---------------------------------------------------------------------------------- */

int mm_device_init(mm_device *d, const mm_device_config *cfg, char *error, size_t error_cap)
{
    memset(d, 0, sizeof *d);
    d->cfg = *cfg;
    d->acked_through = -1;
    if (!cfg->mound_id || !cfg->secret_key || !cfg->controller_public_key || !cfg->new_id) {
        if (error_cap) set_str(error, error_cap, "device config: mound_id, secret_key, controller_public_key and new_id are required");
        return -1;
    }
    return mm_kernel_init(&d->kernel, cfg->mound_id, cfg->caps, cfg->n_caps, cfg->routines, cfg->n_routines, error, error_cap);
}

/* ---- uplink -------------------------------------------------------------------------------- */

int mm_device_publish(mm_device *d, const char *kind, mm_body_writer body, const void *body_ctx, int64_t now)
{
    mm_queued *q;
    mm_envelope e;
    char id[MM_ID_CAP], sent_at[MM_TIME_TEXT_CAP];
    size_t len;

    if (d->queue_len >= MM_DEVICE_QUEUE) {
        audit(d, "uplink queue full: nothing more is recorded until the controller acknowledges");
        return -1;
    }
    q = queue_slot(d, d->queue_len);

    d->cfg.new_id(d->cfg.new_id_ctx, id);
    mm_time_format(now, sent_at, sizeof sent_at);

    memset(&e, 0, sizeof e);
    e.id = id;
    e.mound_id = d->cfg.mound_id;
    e.seq = d->next_seq;
    e.sent_at = sent_at;
    e.kind = kind;
    e.prev_digest = d->last_digest;
    e.body = body;
    e.body_ctx = body_ctx;

    len = mm_envelope_write_signed(&e, d->cfg.secret_key, q->wire, sizeof q->wire, q->digest);
    if (len == 0) {
        audit(d, "uplink envelope does not fit the wire buffer; not recorded");
        return -1;
    }
    q->len = len;
    q->seq = d->next_seq;
    d->queue_len++;
    d->next_seq++;
    set_str(d->last_digest, sizeof d->last_digest, q->digest);
    return 0;
}

long long mm_device_beat(mm_device *d, int64_t now)
{
    mm_mound_sync body;
    long long seq = d->next_seq;
    body.state = mm_device_state(d);
    body.queue_depth = (long long)d->queue_len;
    return mm_device_publish(d, MM_KIND_MOUND_SYNC, mm_body_mound_sync, &body, now) == 0 ? seq : -1;
}

int mm_device_act(mm_device *d, const mm_request *request, int64_t now, mm_action_record_in *record)
{
    char id[MM_ID_CAP];
    mm_action_record_view view;
    d->cfg.new_id(d->cfg.new_id_ctx, id);
    mm_kernel_execute(&d->kernel, request, now, id, record);
    mm_action_record_bind(record, &view);
    return mm_device_publish(d, MM_KIND_ACTION_RECORD, mm_body_action_record, &view.record, now);
}

static void acknowledge_through(mm_device *d, long long seq)
{
    if (seq <= d->acked_through) return;      /* a stale or duplicate ack moves nothing backwards */
    d->acked_through = seq;
    while (d->queue_len > 0 && queue_slot(d, 0)->seq <= seq) {
        d->queue_head = (d->queue_head + 1) % MM_DEVICE_QUEUE;
        d->queue_len--;
    }
}

/* ---- downlink ------------------------------------------------------------------------------ */

static int seen(mm_device *d, const char *id)
{
    size_t i;
    for (i = 0; i < MM_DEVICE_HANDLED_IDS; i++)
        if (strcmp(d->handled[i], id) == 0) return 1;
    return 0;
}

static void remember(mm_device *d, const char *id)
{
    set_str(d->handled[d->handled_next], MM_ID_CAP, id);
    d->handled_next = (d->handled_next + 1) % MM_DEVICE_HANDLED_IDS;
}

static void publish_ack(mm_device *d, const char *status, const char *refers_to, const char *detail, int64_t now)
{
    mm_ack ack;
    memset(&ack, 0, sizeof ack);
    ack.status = status;
    ack.refers_to = refers_to;
    ack.through_seq = -1;
    ack.detail = detail;
    mm_device_publish(d, MM_KIND_ACK, mm_body_ack, &ack, now);
}

/* RunnerAnt.Verify: signature, addressing, shape — dropped and audited, never processed, otherwise. */
static int verify(mm_device *d, const mm_wire *w, mm_envelope_in *frame)
{
    char line[MM_DETAIL_CAP], joined[MM_REASON_CAP];   /* composed here whole; audit() bounds it to MM_REASON_CAP */
    mm_refusal why;
    int err;

    if (mm_envelope_verify_wire(w->bytes, w->n, d->cfg.controller_public_key, NULL) != 0) {
        audit(d, "downlink dropped: signature does not verify under the controller key, or the envelope is not canonical");
        return 0;
    }
    if (mm_envelope_parse(w->bytes, w->n, frame, &err) != 0) {
        snprintf(line, sizeof line, "downlink dropped: frame unreadable (%s)", mm_jr_error_name(err));
        audit(d, line);
        return 0;
    }
    if (seen(d, frame->id)) return 0;                               /* re-delivery: idempotent, silent */
    if (strcmp(frame->mound_id, d->cfg.mound_id) != 0) {
        snprintf(line, sizeof line, "downlink %.63s (%.23s) dropped: addressed to '%.63s', this mound is '%.63s'",
                 frame->id, frame->kind, frame->mound_id, d->cfg.mound_id);   /* precisions = the fields' caps, for gcc's truncation heuristic */
        audit(d, line);
        return 0;
    }
    if (mm_envelope_validate(frame, &why) != 0) {
        snprintf(line, sizeof line, "downlink %s (%s) dropped: %s", frame->id, frame->kind, mm_refusal_join(&why, joined, sizeof joined));
        audit(d, line);
        return 0;
    }
    return 1;
}

static int handle_ack(mm_device *d, const mm_envelope_in *frame, long long beat_seq)
{
    mm_ack_in ack;
    int err;
    remember(d, frame->id);
    if (mm_ack_parse(frame->body, frame->body_len, &ack, &err) != 0 || ack.through_seq < 0) return 0;
    acknowledge_through(d, ack.through_seq);
    return beat_seq >= 0 && ack.through_seq >= beat_seq;   /* a beat that could not be queued is never "acknowledged" */
}

static int order_of(const char *kind)
{
    if (strcmp(kind, MM_KIND_STOP) == 0) return 0;
    if (strcmp(kind, MM_KIND_CHARTER) == 0) return 1;
    return 4;
}

static void handle_deferred(mm_device *d, const mm_envelope_in *frame, int64_t now)
{
    char detail[MM_REASON_CAP + 32], joined[MM_REASON_CAP];

    if (seen(d, frame->id)) return;                                 /* two copies in one batch */
    remember(d, frame->id);

    if (strcmp(frame->kind, MM_KIND_STOP) == 0) {
        mm_authority_stop(&d->kernel.authority);
        if (d->cfg.enter_safe_state) d->cfg.enter_safe_state(d->cfg.safe_state_ctx);
        publish_ack(d, "ok", frame->id, "stopped; actuation ceased, sensing and syncing continue", now);
        return;
    }
    if (strcmp(frame->kind, MM_KIND_CHARTER) == 0) {
        mm_charter_in charter;
        mm_refusal why;
        int err;
        if (mm_charter_parse(frame->body, frame->body_len, &charter, &err) != 0) {
            publish_ack(d, "refused", frame->id, "charter refused: charter body unreadable", now);
            return;
        }
        if (mm_kernel_accept_charter(&d->kernel, &charter, now, &why) != 0) {
            snprintf(detail, sizeof detail, "charter refused: %s", mm_refusal_join(&why, joined, sizeof joined));
            publish_ack(d, "refused", frame->id, detail, now);
        }
        return;                                                     /* an accepted charter is not acknowledged; the next beat says "chartered" */
    }
    snprintf(detail, sizeof detail, "'%s' is not a downlink kind this mound processes", frame->kind);
    publish_ack(d, "refused_unknown_kind", frame->id, detail, now);
}

int mm_device_receive_batch(mm_device *d, const mm_wire *downlink, size_t n, int64_t now, long long beat_seq, int *beat_acknowledged)
{
    mm_envelope_in frames[MM_DEVICE_BATCH];
    size_t deferred[MM_DEVICE_BATCH], n_deferred = 0, i, j;
    int handled = 0;

    if (n > MM_DEVICE_BATCH) n = MM_DEVICE_BATCH;
    for (i = 0; i < n; i++) {
        if (!verify(d, &downlink[i], &frames[i])) continue;
        if (strcmp(frames[i].kind, MM_KIND_ACK) == 0) {
            if (handle_ack(d, &frames[i], beat_seq) && beat_acknowledged) *beat_acknowledged = 1;
            handled++;
        } else {
            deferred[n_deferred++] = i;
        }
    }

    /* stops first, then charters, then the rest — a stable insertion sort, as OrderBy is stable */
    for (i = 1; i < n_deferred; i++) {
        size_t v = deferred[i];
        for (j = i; j > 0 && order_of(frames[deferred[j - 1]].kind) > order_of(frames[v].kind); j--) deferred[j] = deferred[j - 1];
        deferred[j] = v;
    }
    for (i = 0; i < n_deferred; i++) {
        handle_deferred(d, &frames[deferred[i]], now);
        handled++;
    }
    return handled;
}

/* ---- the beat ------------------------------------------------------------------------------ */

void mm_device_sync(mm_device *d, int64_t now, mm_exchange_fn exchange, void *ctx, mm_sync_outcome *out)
{
    long long beat_seq;
    mm_wire downlink[MM_DEVICE_BATCH];
    size_t n_downlink;

    memset(out, 0, sizeof *out);
    beat_seq = mm_device_beat(d, now);

    /*
     * Drain oldest-first: one envelope up, whatever came down handled; stop when an exchange moved
     * nothing. Only what was queued when the beat was published goes up in this beat: the acks a
     * stop or a refused charter provoke wait for the next one, as they do on a Pi (RunnerAnt handles
     * deferred downlink after the drain settles). Downlink is handled per exchange here, stops first
     * within an exchange, because a device keeps no second copy of what came down.
     */
    for (;;) {
        const mm_queued *oldest;
        long long acked_before = d->acked_through;
        if (d->queue_len == 0) break;
        oldest = queue_slot(d, 0);
        if (beat_seq >= 0 && oldest->seq > beat_seq) break;
        n_downlink = 0;
        if (exchange(ctx, oldest->wire, oldest->len, downlink, &n_downlink) != 0) {
            d->connected = 0;
            out->delivered = 0;
            return;
        }
        out->envelopes_sent++;
        out->downlink_handled += mm_device_receive_batch(d, downlink, n_downlink, now, beat_seq, &out->beat_acknowledged);
        if (d->acked_through <= acked_before) break;   /* nothing acknowledged: everything was offered once */
    }

    d->connected = 1;
    d->last_sync_at = now;
    out->delivered = 1;
    if (out->beat_acknowledged) mm_authority_renew_lease(&d->kernel.authority, now);   /* §5: the ACKNOWLEDGED beat renews */
}

int mm_device_tick(mm_device *d, int64_t now)
{
    if (mm_authority_quiesce_if_expired(&d->kernel.authority, now)) {
        if (d->cfg.enter_safe_state) d->cfg.enter_safe_state(d->cfg.safe_state_ctx);
        audit(d, "lease expired: quiesced to the safe state; awaiting a fresh charter");
        return 1;
    }
    return 0;
}

/* ---- views --------------------------------------------------------------------------------- */

size_t mm_device_queue_depth(const mm_device *d) { return d->queue_len; }

const mm_queued *mm_device_queue_at(const mm_device *d, size_t i)
{
    if (i >= d->queue_len) return NULL;
    return &d->queue[(d->queue_head + i) % MM_DEVICE_QUEUE];
}

const char *mm_device_state(const mm_device *d) { return mm_authority_state(&d->kernel.authority); }

const char *mm_device_audit_at(const mm_device *d, size_t i) { return i < d->audit_count ? d->audit[i] : NULL; }

size_t mm_device_audit_count(const mm_device *d) { return d->audit_count; }
