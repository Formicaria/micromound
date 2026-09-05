#include "mm_bodies.h"

static void write_string_array(mm_json *w, const char *key, const char *const *items, size_t n)
{
    size_t i;
    mm_json_key(w, key);
    mm_json_array_begin(w);
    for (i = 0; i < n; i++) mm_json_string(w, items[i]);
    mm_json_array_end(w);
}

/* Dictionary<string, double>: an object in insertion order; {} when empty. */
static void write_params(mm_json *w, const char *key, const mm_param *params, size_t n)
{
    size_t i;
    mm_json_key(w, key);
    mm_json_object_begin(w);
    for (i = 0; i < n; i++) mm_json_kv_double(w, params[i].key, params[i].value);
    mm_json_object_end(w);
}

static void write_opt_double(mm_json *w, const char *key, const mm_opt_double *v)
{
    if (v->present) mm_json_kv_double(w, key, v->value);
    else mm_json_kv_null(w, key);
}

/* ---- mound_sync ---- */

void mm_body_mound_sync(mm_json *w, const void *ctx)
{
    const mm_mound_sync *b = (const mm_mound_sync *)ctx;
    mm_json_object_begin(w);
    mm_json_kv_string(w, "state", b->state);
    mm_json_kv_int(w, "queue_depth", b->queue_depth);
    mm_json_object_end(w);
}

/* ---- action_record: Micromound.Protocol.ActionRecord, field for field ---- */

void mm_body_action_record(mm_json *w, const void *ctx)
{
    const mm_action_record *b = (const mm_action_record *)ctx;
    mm_json_object_begin(w);
    mm_json_kv_string(w, "action_id", b->action_id);
    mm_json_kv_string(w, "mission_id", b->mission_id);
    mm_json_kv_string(w, "charter_id", b->charter_id);
    mm_json_kv_string(w, "capability", b->capability);
    mm_json_kv_string(w, "routine_id", b->routine_id);
    write_params(w, "requested_parameters", b->requested_parameters, b->n_requested_parameters);
    write_params(w, "parameters", b->parameters, b->n_parameters);
    mm_json_kv_string(w, "started_at", b->started_at);
    mm_json_kv_string(w, "ended_at", b->ended_at);
    mm_json_kv_string(w, "outcome", b->outcome);
    mm_json_kv_bool(w, "evidence_required", b->evidence_required);
    write_string_array(w, "evidence_refs", b->evidence_refs, b->n_evidence_refs);
    mm_json_kv_string(w, "detail", b->detail);
    mm_json_object_end(w);
}

/* ---- ack: Micromound.Protocol.AckBody ---- */

void mm_body_ack(mm_json *w, const void *ctx)
{
    const mm_ack *b = (const mm_ack *)ctx;
    mm_json_object_begin(w);
    mm_json_kv_string(w, "status", b->status);
    mm_json_kv_string(w, "refers_to", b->refers_to);
    mm_json_kv_int(w, "through_seq", b->through_seq);
    write_string_array(w, "evidence_ids", b->evidence_ids, b->n_evidence_ids);
    mm_json_kv_string(w, "detail", b->detail);
    mm_json_object_end(w);
}

/* ---- charter: Micromound.Protocol.Charter ---- */

void mm_write_capability_limits(mm_json *w, const mm_capability_limits *limits)
{
    mm_json_object_begin(w);
    write_opt_double(w, "max_on_s", &limits->max_on_s);
    write_opt_double(w, "min_off_s", &limits->min_off_s);
    write_opt_double(w, "min", &limits->min);
    write_opt_double(w, "max", &limits->max);
    write_opt_double(w, "max_rate_per_h", &limits->max_rate_per_h);
    mm_json_object_end(w);
}

void mm_body_charter(mm_json *w, const void *ctx)
{
    const mm_charter *b = (const mm_charter *)ctx;
    size_t i;

    mm_json_object_begin(w);
    mm_json_kv_string(w, "charter_id", b->charter_id);
    mm_json_kv_string(w, "mound_id", b->mound_id);
    mm_json_kv_string(w, "mission_ref", b->mission_ref);
    mm_json_kv_string(w, "issued_at", b->issued_at);
    mm_json_kv_string(w, "expires_at", b->expires_at);
    mm_json_kv_int(w, "lease_ttl_s", b->lease_ttl_s);
    mm_json_kv_string(w, "action_ceiling", b->action_ceiling);
    write_string_array(w, "capabilities", b->capabilities, b->n_capabilities);
    write_string_array(w, "routines", b->routines, b->n_routines);

    mm_json_key(w, "limits");
    mm_json_object_begin(w);
    for (i = 0; i < b->n_limits; i++) {
        mm_json_key(w, b->limits[i].capability);
        mm_write_capability_limits(w, &b->limits[i].limits);
    }
    mm_json_object_end(w);

    mm_json_key(w, "evidence");
    mm_json_object_begin(w);
    write_string_array(w, "required_for", b->evidence_required_for, b->n_evidence_required_for);
    mm_json_kv_int(w, "min_interval_s", b->evidence_min_interval_s);
    mm_json_object_end(w);

    mm_json_kv_string(w, "safe_state", b->safe_state);
    mm_json_kv_int(w, "sync_interval_s", b->sync_interval_s);
    mm_json_object_end(w);
}
