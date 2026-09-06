/*
 * mm_device — a reduced-profile mound, end to end: the Runner Ant's loop over the kernel.
 *
 * What RecordAnts.RunnerAnt does on a Pi, for a device whose tables are compiled in:
 *
 *   uplink    mm_device_publish builds an envelope (id, seq, sent_at, kind, body, prev_digest),
 *             signs it and chains it onto a bounded queue; mm_device_beat publishes the mound_sync
 *             beat {state, queue_depth}; mm_device_act runs a request through the kernel and
 *             publishes the action_record.
 *   downlink  mm_device_receive_batch takes what one exchange returned: every envelope is verified
 *             from the bytes as received under the controller's key, addressed to this mound,
 *             shaped for the reduced profile, and seen once (an idempotency window) — or dropped
 *             and audited, never processed and never acknowledged. Acks are handled inline (they
 *             drive the drain); everything else is deferred and then handled stops first, charters
 *             second, the rest after — a stop in a batch precedes a charter in the same batch,
 *             wherever each arrived. A stop enters the safe state and is acknowledged; a charter is
 *             accepted silently or refused with the validator's reasons in a `refused` ack; any
 *             other kind is acknowledged `refused_unknown_kind`.
 *   the beat  mm_device_sync: publish the beat, then drain the queue oldest-first through the
 *             transport, one envelope up and an array down per exchange, until nothing is
 *             acknowledged (PROTOCOL.md §5: it is the ACKNOWLEDGED beat that renews the lease).
 *   the tick  mm_device_tick: quiesce when the lease runs out (and enter the safe state).
 *
 * Fixed memory: MM_DEVICE_QUEUE envelopes of MM_DEVICE_WIRE_CAP bytes. A full queue refuses to
 * publish — a device that cannot reach its controller stops recording rather than overwriting
 * unacknowledged proof; the beat still goes when there is room for it.
 *
 * Transcript: tests/Micromound.Tests/Golden/files/device-session.txt — a scripted session written
 * by tests/test_device.c and read back by the C# DeviceSessionTests, which verifies every uplink
 * envelope with the host's verifier and chain validator.
 */
#ifndef MM_DEVICE_H
#define MM_DEVICE_H

#include <stddef.h>
#include <stdint.h>
#include "mm_decode.h"
#include "mm_kernel.h"

#ifdef __cplusplus
extern "C" {
#endif

#ifndef MM_DEVICE_QUEUE
#define MM_DEVICE_QUEUE 16
#endif
#ifndef MM_DEVICE_WIRE_CAP
#define MM_DEVICE_WIRE_CAP 2048
#endif
#define MM_DEVICE_HANDLED_IDS 32
#define MM_DEVICE_AUDIT 8
#define MM_DEVICE_BATCH 16

/* A queued uplink envelope: the signed wire bytes and what the chain needs to know about it. */
typedef struct mm_queued {
    char wire[MM_DEVICE_WIRE_CAP];
    size_t len;
    long long seq;
    char digest[MM_DIGEST_TEXT_LEN + 1];
} mm_queued;

/* One received wire envelope. */
typedef struct mm_wire {
    const char *bytes;
    size_t n;
} mm_wire;

/*
 * ISyncTransport.TryExchange: send one envelope, receive what came down. Returns 0 and fills
 * downlink[0..*n_downlink) (at most MM_DEVICE_BATCH; the bytes must stay valid until the next call),
 * or -1 when the transport is unavailable — offline is a normal state, not an error.
 */
typedef int (*mm_exchange_fn)(void *ctx, const char *wire, size_t n, mm_wire *downlink, size_t *n_downlink);

/* Fresh envelope ids: a UUID string into out. A device uses its RNG; a test uses a counter. */
typedef void (*mm_new_id_fn)(void *ctx, char out[MM_ID_CAP]);

typedef struct mm_device_config {
    const char *mound_id;
    const uint8_t *secret_key;              /* 64 bytes, mm_ed25519_seed_keypair's sk */
    const uint8_t *controller_public_key;   /* 32 bytes, received at enrollment */
    const mm_capability_desc *caps;   size_t n_caps;
    const mm_routine_desc *routines;  size_t n_routines;
    mm_new_id_fn new_id; void *new_id_ctx;
    void (*enter_safe_state)(void *ctx);    /* de-energize; called on stop and on quiesce (may be NULL) */
    void *safe_state_ctx;
} mm_device_config;

typedef struct mm_device {
    mm_device_config cfg;
    mm_kernel kernel;

    /* the uplink chain */
    mm_queued queue[MM_DEVICE_QUEUE];
    size_t queue_head, queue_len;
    long long next_seq;
    long long acked_through;
    char last_digest[MM_DIGEST_TEXT_LEN + 1];

    /* the idempotency window for downlink ids */
    char handled[MM_DEVICE_HANDLED_IDS][MM_ID_CAP];
    size_t handled_next;

    /* the last few audit lines, newest last */
    char audit[MM_DEVICE_AUDIT][MM_REASON_CAP];
    size_t audit_count;

    int connected;
    int64_t last_sync_at;
} mm_device;

typedef struct mm_sync_outcome {
    int delivered;
    int envelopes_sent;
    int downlink_handled;
    int beat_acknowledged;
} mm_sync_outcome;

int mm_device_init(mm_device *d, const mm_device_config *cfg, char *error, size_t error_cap);

/* Bind executors and device limits through d->kernel (mm_kernel_bind_executor, mm_kernel_apply_device_limits). */

/* Publish one signed, chained envelope. 0, or -1 when the queue is full or the body does not fit. */
int mm_device_publish(mm_device *d, const char *kind, mm_body_writer body, const void *body_ctx, int64_t now);

/* The mound_sync beat. Returns its seq, or -1 when it could not be queued. */
long long mm_device_beat(mm_device *d, int64_t now);

/* Run a request through the kernel and publish its action_record. Returns the publish result; the record is copied out. */
int mm_device_act(mm_device *d, const mm_request *request, int64_t now, mm_action_record_in *record);

/* Handle a batch of downlink envelopes (one exchange's worth). Returns how many were handled (acks included). */
int mm_device_receive_batch(mm_device *d, const mm_wire *downlink, size_t n, int64_t now, long long beat_seq, int *beat_acknowledged);

/* The whole beat: publish, drain through the transport, handle what came down, renew the lease on an acknowledged beat. */
void mm_device_sync(mm_device *d, int64_t now, mm_exchange_fn exchange, void *ctx, mm_sync_outcome *out);

/* Housekeeping between beats: quiesce (and enter the safe state) when the lease has run out. 1 when it quiesced now. */
int mm_device_tick(mm_device *d, int64_t now);

size_t mm_device_queue_depth(const mm_device *d);
const mm_queued *mm_device_queue_at(const mm_device *d, size_t i);   /* i = 0 is the oldest */
const char *mm_device_state(const mm_device *d);
const char *mm_device_audit_at(const mm_device *d, size_t i);        /* i = 0 is the oldest kept */
size_t mm_device_audit_count(const mm_device *d);

#ifdef __cplusplus
}
#endif

#endif
