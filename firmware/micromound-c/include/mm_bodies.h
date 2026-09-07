/*
 * mm_bodies — the typed bodies of the reduced profile (PROTOCOL.md §8), encoded field for field
 * as the C# contracts in src/Micromound.Protocol serialize them: same names, same order, every
 * field present, C# null as JSON null, empty collections as {} / [].
 *
 * A device EMITS mound_sync, action_record and ack, and must be able to reproduce a charter's
 * bytes to verify one it received (the charter's own signature covers its envelope; reproducing
 * the body is how the field layout is pinned for the parser that lands with the firmware).
 * mission, mission_report, evidence_bundle and config are deliberately absent — §8.
 *
 * Every writer here has the mm_body_writer signature so it plugs straight into mm_envelope.
 *
 * Fixture: tests/Micromound.Tests/Golden/files/canonical-bodies.txt.
 */
#ifndef MM_BODIES_H
#define MM_BODIES_H

#include <stddef.h>
#include "mm_json.h"

#ifdef __cplusplus
extern "C" {
#endif

/* One entry of a Dictionary<string, double>. */
typedef struct mm_param {
    const char *key;
    double value;
} mm_param;

/* A C# double? — present or JSON null. */
typedef struct mm_opt_double {
    int present;
    double value;
} mm_opt_double;

/* ---- mound_sync (uplink, every beat; RecordAnts.Sync in the runtime) ---- */
typedef struct mm_mound_sync {
    const char *state;          /* "idle" | "chartered" | … : the mound state name */
    long long queue_depth;
    long long spilled_envelopes;   /* records dropped under pressure; 0 on a device that refuses instead */
} mm_mound_sync;
void mm_body_mound_sync(mm_json *w, const void *ctx);

/* ---- evidence item (Micromound.Protocol.EvidenceItem), as it rides inline on an action_record ---- */
typedef struct mm_evidence_item {
    const char *evidence_id;
    const char *type;                       /* reading | sensor_window | image_ref | telemetry_summary | outcome_code */
    const char *captured_at;
    const char *source;
    const char *payload_json;               /* a JSON document as a STRING (escaped on the wire); "" when none */
    const char *content_digest;             /* "" unless image_ref */
} mm_evidence_item;
void mm_write_evidence_item(mm_json *w, const mm_evidence_item *item);

/* ---- evidence_bundle (uplink, full profile only; Micromound.Protocol.EvidenceBundle). Not in the
   reduced profile (§8) — here so the item shape is pinned against the golden bundle. ---- */
typedef struct mm_evidence_bundle {
    const char *bundle_id;
    const mm_evidence_item *items;
    size_t n_items;
    long long evicted_acked_items;
    long long spilled_unacked_items;
} mm_evidence_bundle;
void mm_body_evidence_bundle(mm_json *w, const void *ctx);

/* ---- action_record (uplink; Micromound.Protocol.ActionRecord) ---- */
typedef struct mm_action_record {
    const char *action_id;
    const char *mission_id;                 /* "" on a controller */
    const char *charter_id;
    const char *capability;
    const char *routine_id;                 /* "" when no compiled routine ran */
    const mm_param *requested_parameters;   /* what was asked for, before clamping */
    size_t n_requested_parameters;
    const mm_param *parameters;             /* what was applied */
    size_t n_parameters;
    const char *started_at;
    const char *ended_at;
    const char *outcome;                    /* succeeded | failed | refused | unverified */
    int evidence_required;
    const char *const *evidence_refs;
    size_t n_evidence_refs;
    const mm_evidence_item *evidence;       /* the referenced items inline (a device); NULL/0 on a Pi (they travel by bundle) */
    size_t n_evidence;
    const char *detail;
} mm_action_record;
void mm_body_action_record(mm_json *w, const void *ctx);

/* ---- ack (both directions; Micromound.Protocol.AckBody) ---- */
typedef struct mm_ack {
    const char *status;                     /* ok | refused_unknown_kind */
    const char *refers_to;                  /* "" on a purely cumulative ack */
    long long through_seq;                  /* cumulative, inclusive; negative acknowledges nothing */
    const char *const *evidence_ids;
    size_t n_evidence_ids;
    const char *detail;
} mm_ack;
void mm_body_ack(mm_json *w, const void *ctx);

/* ---- stop (downlink; the controller's anonymous { reason } body, PROTOCOL.md §7) ---- */
typedef struct mm_stop {
    const char *reason;
} mm_stop;
void mm_body_stop(mm_json *w, const void *ctx);

/* ---- charter (downlink; Micromound.Protocol.Charter) ---- */
typedef struct mm_capability_limits {
    mm_opt_double max_on_s;
    mm_opt_double min_off_s;
    mm_opt_double min;
    mm_opt_double max;
    mm_opt_double max_rate_per_h;
} mm_capability_limits;

typedef struct mm_limit_entry {
    const char *capability;
    mm_capability_limits limits;
} mm_limit_entry;

typedef struct mm_charter {
    const char *charter_id;
    const char *mound_id;
    const char *mission_ref;
    const char *issued_at;
    const char *expires_at;
    long long lease_ttl_s;
    const char *action_ceiling;             /* observe | benign | hazardous is never legal */
    const char *const *capabilities;
    size_t n_capabilities;
    const char *const *routines;
    size_t n_routines;
    const mm_limit_entry *limits;           /* in the order the host wrote them */
    size_t n_limits;
    const char *const *evidence_required_for;
    size_t n_evidence_required_for;
    long long evidence_min_interval_s;
    const char *safe_state;
    long long sync_interval_s;
} mm_charter;
void mm_body_charter(mm_json *w, const void *ctx);

/* The limits object on its own, as it appears under "limits":{"cap":{…}}. */
void mm_write_capability_limits(mm_json *w, const mm_capability_limits *limits);

#ifdef __cplusplus
}
#endif

#endif
