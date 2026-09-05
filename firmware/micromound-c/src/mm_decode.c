#include "mm_decode.h"
#include "mm_ed25519.h"
#include "mm_json_read.h"
#include "mm_sha256.h"

#include <stdio.h>
#include <string.h>

/* ---- helpers ------------------------------------------------------------------------------ */

static int finish(mm_jr *r, int rc, int *error)
{
    if (rc == 0) rc = mm_jr_end(r);
    if (error) *error = r->error;
    return rc == 0 ? 0 : -1;
}

/* A string member; JSON null reads as "" (a C# string property set to null re-encodes as null, but
   the contracts initialise every string to "" and the hosts never emit null for one). */
static int read_str(mm_jr *r, char *out, size_t cap)
{
    if (mm_jr_peek(r) == 'n') { if (mm_jr_null(r) != 0) return -1; out[0] = '\0'; return 0; }
    return mm_jr_string(r, out, cap);
}

/* A List<string> into a fixed table. */
static int read_str_array(mm_jr *r, char *table, size_t stride, size_t max, size_t *count)
{
    int more;
    *count = 0;
    if (mm_jr_peek(r) == 'n') return mm_jr_null(r);
    if (mm_jr_array_begin(r) != 0) return -1;
    while ((more = mm_jr_array_next(r)) == 1) {
        if (*count >= max) { r->error = MM_JR_TOO_MANY; return -1; }
        if (read_str(r, table + *count * stride, stride) != 0) return -1;
        (*count)++;
    }
    return more;
}

static int read_opt_double(mm_jr *r, mm_opt_double *out)
{
    if (mm_jr_peek(r) == 'n') { out->present = 0; out->value = 0; return mm_jr_null(r); }
    out->present = 1;
    return mm_jr_double(r, &out->value);
}

static int read_limits(mm_jr *r, mm_capability_limits *out)
{
    char key[MM_NAME_CAP];
    int more;
    memset(out, 0, sizeof *out);
    if (mm_jr_object_begin(r) != 0) return -1;
    while ((more = mm_jr_object_next(r, key, sizeof key)) == 1) {
        int rc;
        if (strcmp(key, "max_on_s") == 0) rc = read_opt_double(r, &out->max_on_s);
        else if (strcmp(key, "min_off_s") == 0) rc = read_opt_double(r, &out->min_off_s);
        else if (strcmp(key, "min") == 0) rc = read_opt_double(r, &out->min);
        else if (strcmp(key, "max") == 0) rc = read_opt_double(r, &out->max);
        else if (strcmp(key, "max_rate_per_h") == 0) rc = read_opt_double(r, &out->max_rate_per_h);
        else rc = mm_jr_skip(r);
        if (rc != 0) return -1;
    }
    return more;
}

static void set_str(char *dst, size_t cap, const char *src)
{
    size_t n = strlen(src);
    if (n >= cap) n = cap - 1;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

/* ---- the envelope frame ------------------------------------------------------------------ */

int mm_envelope_parse(const char *json, size_t n, mm_envelope_in *out, int *error)
{
    mm_jr r;
    char key[64];
    int more, rc = 0;

    memset(out, 0, sizeof *out);
    out->v = MM_PROTOCOL_VERSION;   /* the C# default; a frame without v is the current version */
    out->body = "";

    mm_jr_init(&r, json, n);
    if (mm_jr_object_begin(&r) != 0) return finish(&r, -1, error);
    while ((more = mm_jr_object_next(&r, key, sizeof key)) == 1) {
        if (strcmp(key, "v") == 0) rc = mm_jr_int(&r, &out->v);
        else if (strcmp(key, "id") == 0) rc = read_str(&r, out->id, sizeof out->id);
        else if (strcmp(key, "mound_id") == 0) rc = read_str(&r, out->mound_id, sizeof out->mound_id);
        else if (strcmp(key, "seq") == 0) rc = mm_jr_int(&r, &out->seq);
        else if (strcmp(key, "sent_at") == 0) rc = read_str(&r, out->sent_at, sizeof out->sent_at);
        else if (strcmp(key, "kind") == 0) rc = read_str(&r, out->kind, sizeof out->kind);
        else if (strcmp(key, "body") == 0) rc = mm_jr_raw(&r, &out->body, &out->body_len);
        else if (strcmp(key, "prev_digest") == 0) rc = read_str(&r, out->prev_digest, sizeof out->prev_digest);
        else if (strcmp(key, "sig") == 0) rc = read_str(&r, out->sig, sizeof out->sig);
        else rc = mm_jr_skip(&r);
        if (rc != 0) break;
    }
    return finish(&r, rc == 0 && more == 0 ? 0 : -1, error);
}

int mm_envelope_verify_wire(const char *wire, size_t n, const uint8_t pk[32], char digest_out[MM_DIGEST_TEXT_LEN + 1])
{
    static const char SIG_KEY[] = ",\"sig\":\"";                 /* 8 bytes */
    static const size_t TAIL = 8 + MM_SIG_TEXT_LEN + 2;           /* ,"sig":"<sig>"} */
    const char *sig_text;
    uint8_t sig[MM_ED25519_SIGNATURE_LEN];
    mm_part parts[2];
    size_t prefix_len;

    if (n < TAIL + 1) return -1;
    if (wire[n - 1] != '}' || wire[n - 2] != '"') return -1;
    if (memcmp(wire + n - TAIL, SIG_KEY, 8) != 0) return -1;
    sig_text = wire + n - TAIL + 8;
    if (memcmp(sig_text, "ed25519:", 8) != 0) return -1;
    if (mm_hex_parse(sig_text + 8, sizeof sig, sig) != 0) return -1;

    /* canonical = wire[0 .. prefix_len) + "\"}"  where the prefix ends with ,"sig":"  */
    prefix_len = n - TAIL + 8;
    parts[0].data = (const uint8_t *)wire;
    parts[0].n = prefix_len;
    parts[1].data = (const uint8_t *)"\"}";
    parts[1].n = 2;

    if (mm_ed25519_verify_parts(sig, parts, 2, pk) != 0) return -1;

    if (digest_out) {
        mm_sha256 ctx;
        uint8_t hash[MM_SHA256_DIGEST_LEN];
        mm_sha256_init(&ctx);
        mm_sha256_update(&ctx, parts[0].data, parts[0].n);
        mm_sha256_update(&ctx, parts[1].data, parts[1].n);
        mm_sha256_final(&ctx, hash);
        memcpy(digest_out, "sha256:", 7);
        mm_hex_lower(hash, sizeof hash, digest_out + 7);
    }
    return 0;
}

/* ---- refusals ------------------------------------------------------------------------------ */

static void refuse(mm_refusal *out, const char *reason)
{
    if (out->count < MM_REFUSAL_MAX) set_str(out->reasons[out->count], MM_REASON_CAP, reason);
    out->count++;
}

/* printf-style reason with one or two string arguments — the shapes the validators use. */
static void refuse1(mm_refusal *out, const char *fmt, const char *a)
{
    if (out->count < MM_REFUSAL_MAX) snprintf(out->reasons[out->count], MM_REASON_CAP, fmt, a);
    out->count++;
}

static void refuse2(mm_refusal *out, const char *fmt, const char *a, const char *b)
{
    if (out->count < MM_REFUSAL_MAX) snprintf(out->reasons[out->count], MM_REASON_CAP, fmt, a, b);
    out->count++;
}

const char *mm_refusal_join(const mm_refusal *r, char *out, size_t cap)
{
    size_t len = 0;
    int i;
    if (cap == 0) return out;
    out[0] = '\0';
    for (i = 0; i < r->count && i < MM_REFUSAL_MAX; i++) {
        size_t n = strlen(r->reasons[i]);
        if (i > 0) { if (len + 2 < cap) { memcpy(out + len, "; ", 2); } len += 2; }
        if (len < cap) { size_t room = cap - 1 - len; memcpy(out + len, r->reasons[i], n < room ? n : room); }
        len += n;
    }
    out[len < cap ? len : cap - 1] = '\0';
    return out;
}

static int is_blank(const char *s)
{
    for (; *s; s++) if (*s != ' ' && *s != '\t' && *s != '\n' && *s != '\r') return 0;
    return 1;
}

int mm_envelope_validate(const mm_envelope_in *e, mm_refusal *out)
{
    static const char *const REDUCED[] = { MM_KIND_ENROLL, MM_KIND_MOUND_SYNC, MM_KIND_CHARTER,
                                           MM_KIND_ACTION_RECORD, MM_KIND_STOP, MM_KIND_ACK };
    size_t i;
    int known = 0;
    int64_t t;

    memset(out, 0, sizeof *out);
    if (e->v != MM_PROTOCOL_VERSION) {
        char v[24];
        snprintf(v, sizeof v, "%lld", e->v);
        refuse1(out, "unsupported protocol version %s", v);
    }
    if (is_blank(e->id)) refuse(out, "id missing");
    if (is_blank(e->mound_id)) refuse(out, "mound_id missing");
    if (e->seq < 0) refuse(out, "seq negative");
    for (i = 0; i < sizeof REDUCED / sizeof REDUCED[0]; i++)
        if (strcmp(REDUCED[i], e->kind) == 0) known = 1;
    if (!known) refuse1(out, "refused_unknown_kind: '%s'", e->kind);
    if (mm_time_parse(e->sent_at, &t) != 0) refuse1(out, "sent_at unparseable: '%s'", e->sent_at);
    return out->count;
}

/* ---- bodies -------------------------------------------------------------------------------- */

int mm_charter_parse(const char *json, size_t n, mm_charter_in *out, int *error)
{
    mm_jr r;
    char key[64];
    int more, rc = 0;

    memset(out, 0, sizeof *out);
    set_str(out->action_ceiling, sizeof out->action_ceiling, "observe");
    set_str(out->safe_state, sizeof out->safe_state, "all_actuators_off");
    out->sync_interval_s = 15;
    out->evidence_min_interval_s = 60;

    mm_jr_init(&r, json, n);
    if (mm_jr_object_begin(&r) != 0) return finish(&r, -1, error);
    while ((more = mm_jr_object_next(&r, key, sizeof key)) == 1) {
        if (strcmp(key, "charter_id") == 0) rc = read_str(&r, out->charter_id, sizeof out->charter_id);
        else if (strcmp(key, "mound_id") == 0) rc = read_str(&r, out->mound_id, sizeof out->mound_id);
        else if (strcmp(key, "mission_ref") == 0) rc = read_str(&r, out->mission_ref, sizeof out->mission_ref);
        else if (strcmp(key, "issued_at") == 0) rc = read_str(&r, out->issued_at, sizeof out->issued_at);
        else if (strcmp(key, "expires_at") == 0) rc = read_str(&r, out->expires_at, sizeof out->expires_at);
        else if (strcmp(key, "lease_ttl_s") == 0) rc = mm_jr_int(&r, &out->lease_ttl_s);
        else if (strcmp(key, "action_ceiling") == 0) rc = read_str(&r, out->action_ceiling, sizeof out->action_ceiling);
        else if (strcmp(key, "capabilities") == 0)
            rc = read_str_array(&r, out->capabilities[0], MM_NAME_CAP, MM_MAX_CAPABILITIES, &out->n_capabilities);
        else if (strcmp(key, "routines") == 0)
            rc = read_str_array(&r, out->routines[0], MM_NAME_CAP, MM_MAX_ROUTINES, &out->n_routines);
        else if (strcmp(key, "limits") == 0) {
            char cap[MM_NAME_CAP];
            int m;
            if (mm_jr_object_begin(&r) != 0) { rc = -1; break; }
            while ((m = mm_jr_object_next(&r, cap, sizeof cap)) == 1) {
                if (out->n_limits >= MM_MAX_LIMITS) { r.error = MM_JR_TOO_MANY; m = -1; break; }
                set_str(out->limits[out->n_limits].capability, MM_NAME_CAP, cap);
                if (read_limits(&r, &out->limits[out->n_limits].limits) != 0) { m = -1; break; }
                out->n_limits++;
            }
            rc = m;
        }
        else if (strcmp(key, "evidence") == 0) {
            char sub[64];
            int m;
            if (mm_jr_object_begin(&r) != 0) { rc = -1; break; }
            while ((m = mm_jr_object_next(&r, sub, sizeof sub)) == 1) {
                int rc2;
                if (strcmp(sub, "required_for") == 0)
                    rc2 = read_str_array(&r, out->evidence_required_for[0], MM_NAME_CAP, MM_MAX_REQUIRED_FOR, &out->n_evidence_required_for);
                else if (strcmp(sub, "min_interval_s") == 0) rc2 = mm_jr_int(&r, &out->evidence_min_interval_s);
                else rc2 = mm_jr_skip(&r);
                if (rc2 != 0) { m = -1; break; }
            }
            rc = m;
        }
        else if (strcmp(key, "safe_state") == 0) rc = read_str(&r, out->safe_state, sizeof out->safe_state);
        else if (strcmp(key, "sync_interval_s") == 0) rc = mm_jr_int(&r, &out->sync_interval_s);
        else rc = mm_jr_skip(&r);
        if (rc != 0) break;
    }
    return finish(&r, rc == 0 && more == 0 ? 0 : -1, error);
}

int mm_stop_parse(const char *json, size_t n, mm_stop_in *out, int *error)
{
    mm_jr r;
    char key[64];
    int more, rc = 0;

    memset(out, 0, sizeof *out);
    mm_jr_init(&r, json, n);
    if (mm_jr_object_begin(&r) != 0) return finish(&r, -1, error);
    while ((more = mm_jr_object_next(&r, key, sizeof key)) == 1) {
        if (strcmp(key, "reason") == 0) rc = read_str(&r, out->reason, sizeof out->reason);
        else rc = mm_jr_skip(&r);
        if (rc != 0) break;
    }
    return finish(&r, rc == 0 && more == 0 ? 0 : -1, error);
}

int mm_ack_parse(const char *json, size_t n, mm_ack_in *out, int *error)
{
    mm_jr r;
    char key[64];
    int more, rc = 0;

    memset(out, 0, sizeof *out);
    set_str(out->status, sizeof out->status, "ok");
    out->through_seq = -1;

    mm_jr_init(&r, json, n);
    if (mm_jr_object_begin(&r) != 0) return finish(&r, -1, error);
    while ((more = mm_jr_object_next(&r, key, sizeof key)) == 1) {
        if (strcmp(key, "status") == 0) rc = read_str(&r, out->status, sizeof out->status);
        else if (strcmp(key, "refers_to") == 0) rc = read_str(&r, out->refers_to, sizeof out->refers_to);
        else if (strcmp(key, "through_seq") == 0) rc = mm_jr_int(&r, &out->through_seq);
        else if (strcmp(key, "evidence_ids") == 0)
            rc = read_str_array(&r, out->evidence_ids[0], MM_ID_CAP, MM_MAX_EVIDENCE_IDS, &out->n_evidence_ids);
        else if (strcmp(key, "detail") == 0) rc = read_str(&r, out->detail, sizeof out->detail);
        else rc = mm_jr_skip(&r);
        if (rc != 0) break;
    }
    return finish(&r, rc == 0 && more == 0 ? 0 : -1, error);
}

/* Dictionary<string, double> into a fixed table, in document order. */
static int read_params(mm_jr *r, mm_param_in *table, size_t max, size_t *count)
{
    char key[MM_NAME_CAP];
    int more;
    *count = 0;
    if (mm_jr_peek(r) == 'n') return mm_jr_null(r);
    if (mm_jr_object_begin(r) != 0) return -1;
    while ((more = mm_jr_object_next(r, key, sizeof key)) == 1) {
        if (*count >= max) { r->error = MM_JR_TOO_MANY; return -1; }
        if (mm_jr_double(r, &table[*count].value) != 0) return -1;
        set_str(table[*count].key, MM_NAME_CAP, key);
        (*count)++;
    }
    return more;
}

int mm_action_record_parse(const char *json, size_t n, mm_action_record_in *out, int *error)
{
    mm_jr r;
    char key[64];
    int more, rc = 0;

    memset(out, 0, sizeof *out);
    set_str(out->outcome, sizeof out->outcome, "unverified");

    mm_jr_init(&r, json, n);
    if (mm_jr_object_begin(&r) != 0) return finish(&r, -1, error);
    while ((more = mm_jr_object_next(&r, key, sizeof key)) == 1) {
        if (strcmp(key, "action_id") == 0) rc = read_str(&r, out->action_id, sizeof out->action_id);
        else if (strcmp(key, "mission_id") == 0) rc = read_str(&r, out->mission_id, sizeof out->mission_id);
        else if (strcmp(key, "charter_id") == 0) rc = read_str(&r, out->charter_id, sizeof out->charter_id);
        else if (strcmp(key, "capability") == 0) rc = read_str(&r, out->capability, sizeof out->capability);
        else if (strcmp(key, "routine_id") == 0) rc = read_str(&r, out->routine_id, sizeof out->routine_id);
        else if (strcmp(key, "requested_parameters") == 0)
            rc = read_params(&r, out->requested_parameters, MM_MAX_PARAMS, &out->n_requested_parameters);
        else if (strcmp(key, "parameters") == 0)
            rc = read_params(&r, out->parameters, MM_MAX_PARAMS, &out->n_parameters);
        else if (strcmp(key, "started_at") == 0) rc = read_str(&r, out->started_at, sizeof out->started_at);
        else if (strcmp(key, "ended_at") == 0) rc = read_str(&r, out->ended_at, sizeof out->ended_at);
        else if (strcmp(key, "outcome") == 0) rc = read_str(&r, out->outcome, sizeof out->outcome);
        else if (strcmp(key, "evidence_required") == 0) rc = mm_jr_bool(&r, &out->evidence_required);
        else if (strcmp(key, "evidence_refs") == 0)
            rc = read_str_array(&r, out->evidence_refs[0], MM_ID_CAP, MM_MAX_EVIDENCE_IDS, &out->n_evidence_refs);
        else if (strcmp(key, "detail") == 0) rc = read_str(&r, out->detail, sizeof out->detail);
        else rc = mm_jr_skip(&r);
        if (rc != 0) break;
    }
    return finish(&r, rc == 0 && more == 0 ? 0 : -1, error);
}

/* ---- validation ---------------------------------------------------------------------------- */

int mm_capability_pattern_matches(const char *pattern, const char *capability)
{
    size_t pl;
    if (pattern == NULL || pattern[0] == '\0') return 0;
    if (strcmp(pattern, "*") == 0) return 1;
    pl = strlen(pattern);
    if (pl >= 2 && pattern[pl - 2] == '.' && pattern[pl - 1] == '*')
        return strncmp(pattern, capability, pl - 1) == 0;     /* "act.*" -> prefix "act." */
    return strcmp(pattern, capability) == 0;
}

int mm_capability_is_routine(const char *id)
{
    return strncmp(id, "routine.", 8) == 0;
}

int mm_action_class_parse(const char *text)
{
    if (strcmp(text, "observe") == 0) return 0;
    if (strcmp(text, "benign") == 0) return 1;
    if (strcmp(text, "controlled") == 0) return 2;
    if (strcmp(text, "hazardous") == 0) return 3;
    return -1;
}

static int in_list(const char *needle, const char *const *list, size_t n)
{
    size_t i;
    for (i = 0; i < n; i++) if (strcmp(list[i], needle) == 0) return 1;
    return 0;
}

static int in_table(const char *needle, const char *table, size_t stride, size_t n)
{
    size_t i;
    for (i = 0; i < n; i++) if (strcmp(table + i * stride, needle) == 0) return 1;
    return 0;
}

int mm_charter_validate(const mm_charter_in *c, const char *expected_mound_id, int64_t now,
                        const char *const *device_capabilities, size_t n_device_capabilities,
                        const char *const *device_routines, size_t n_device_routines,
                        mm_refusal *out)
{
    int64_t expires = 0, issued = 0;
    int expires_ok, issued_ok, ceiling;
    size_t i;

    memset(out, 0, sizeof *out);

    if (is_blank(c->charter_id)) refuse(out, "charter_id missing");
    if (is_blank(c->mound_id)) refuse(out, "mound_id missing");
    else if (strcmp(c->mound_id, expected_mound_id) != 0)
        refuse2(out, "mound_id mismatch: charter is for '%s', this mound is '%s'", c->mound_id, expected_mound_id);

    ceiling = mm_action_class_parse(c->action_ceiling);
    if (ceiling < 0) refuse1(out, "action_ceiling unknown: '%s'", c->action_ceiling);
    else if (ceiling == 3) refuse(out, "action_ceiling 'hazardous' is never a legal charter ceiling");

    expires_ok = mm_time_parse(c->expires_at, &expires) == 0;
    if (!expires_ok) refuse1(out, "expires_at unparseable: '%s'", c->expires_at);
    else if (expires <= now) refuse(out, "charter already expired");

    issued_ok = mm_time_parse(c->issued_at, &issued) == 0;
    if (!issued_ok) refuse1(out, "issued_at unparseable: '%s'", c->issued_at);
    else if (expires_ok && expires <= issued) refuse(out, "expires_at precedes issued_at");

    if (c->lease_ttl_s <= 0) refuse(out, "lease_ttl_s must be positive");
    if (c->sync_interval_s <= 0) refuse(out, "sync_interval_s must be positive");
    if (is_blank(c->safe_state)) refuse(out, "safe_state missing");

    for (i = 0; i < c->n_capabilities; i++) {
        if (mm_capability_is_routine(c->capabilities[i]))
            refuse1(out, "'%s' is a routine and belongs in 'routines', not 'capabilities'", c->capabilities[i]);
        else if (device_capabilities != NULL && !in_list(c->capabilities[i], device_capabilities, n_device_capabilities))
            refuse1(out, "capability '%s' is not physically present on this device", c->capabilities[i]);
    }

    if (device_routines != NULL)
        for (i = 0; i < c->n_routines; i++)
            if (!in_list(c->routines[i], device_routines, n_device_routines))
                refuse1(out, "routine '%s' is not registered on this device", c->routines[i]);

    for (i = 0; i < c->n_limits; i++)
        if (!in_table(c->limits[i].capability, c->capabilities[0], MM_NAME_CAP, c->n_capabilities) &&
            !in_table(c->limits[i].capability, c->routines[0], MM_NAME_CAP, c->n_routines))
            refuse1(out, "limits key '%s' matches no granted capability or routine", c->limits[i].capability);

    return out->count;
}

/* ---- views --------------------------------------------------------------------------------- */

void mm_charter_bind(const mm_charter_in *in, mm_charter_view *v)
{
    size_t i;
    memset(v, 0, sizeof *v);
    for (i = 0; i < in->n_capabilities; i++) v->capabilities[i] = in->capabilities[i];
    for (i = 0; i < in->n_routines; i++) v->routines[i] = in->routines[i];
    for (i = 0; i < in->n_limits; i++) { v->limits[i].capability = in->limits[i].capability; v->limits[i].limits = in->limits[i].limits; }
    for (i = 0; i < in->n_evidence_required_for; i++) v->required_for[i] = in->evidence_required_for[i];

    v->charter.charter_id = in->charter_id;
    v->charter.mound_id = in->mound_id;
    v->charter.mission_ref = in->mission_ref;
    v->charter.issued_at = in->issued_at;
    v->charter.expires_at = in->expires_at;
    v->charter.lease_ttl_s = in->lease_ttl_s;
    v->charter.action_ceiling = in->action_ceiling;
    v->charter.capabilities = v->capabilities;
    v->charter.n_capabilities = in->n_capabilities;
    v->charter.routines = v->routines;
    v->charter.n_routines = in->n_routines;
    v->charter.limits = v->limits;
    v->charter.n_limits = in->n_limits;
    v->charter.evidence_required_for = v->required_for;
    v->charter.n_evidence_required_for = in->n_evidence_required_for;
    v->charter.evidence_min_interval_s = in->evidence_min_interval_s;
    v->charter.safe_state = in->safe_state;
    v->charter.sync_interval_s = in->sync_interval_s;
}

void mm_ack_bind(const mm_ack_in *in, mm_ack_view *v)
{
    size_t i;
    memset(v, 0, sizeof *v);
    for (i = 0; i < in->n_evidence_ids; i++) v->evidence_ids[i] = in->evidence_ids[i];
    v->ack.status = in->status;
    v->ack.refers_to = in->refers_to;
    v->ack.through_seq = in->through_seq;
    v->ack.evidence_ids = v->evidence_ids;
    v->ack.n_evidence_ids = in->n_evidence_ids;
    v->ack.detail = in->detail;
}

void mm_action_record_bind(const mm_action_record_in *in, mm_action_record_view *v)
{
    size_t i;
    memset(v, 0, sizeof *v);
    for (i = 0; i < in->n_requested_parameters; i++) {
        v->requested_parameters[i].key = in->requested_parameters[i].key;
        v->requested_parameters[i].value = in->requested_parameters[i].value;
    }
    for (i = 0; i < in->n_parameters; i++) {
        v->parameters[i].key = in->parameters[i].key;
        v->parameters[i].value = in->parameters[i].value;
    }
    for (i = 0; i < in->n_evidence_refs; i++) v->evidence_refs[i] = in->evidence_refs[i];

    v->record.action_id = in->action_id;
    v->record.mission_id = in->mission_id;
    v->record.charter_id = in->charter_id;
    v->record.capability = in->capability;
    v->record.routine_id = in->routine_id;
    v->record.requested_parameters = v->requested_parameters;
    v->record.n_requested_parameters = in->n_requested_parameters;
    v->record.parameters = v->parameters;
    v->record.n_parameters = in->n_parameters;
    v->record.started_at = in->started_at;
    v->record.ended_at = in->ended_at;
    v->record.outcome = in->outcome;
    v->record.evidence_required = in->evidence_required;
    v->record.evidence_refs = v->evidence_refs;
    v->record.n_evidence_refs = in->n_evidence_refs;
    v->record.detail = in->detail;
}
