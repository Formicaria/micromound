#include "mm_kernel.h"
#include "mm_format.h"
#include "mm_time.h"

#include <stdio.h>
#include <string.h>

/* ---- small helpers ------------------------------------------------------------------------ */

static void set_str(char *dst, size_t cap, const char *src)
{
    size_t n = strlen(src);
    if (n >= cap) n = cap - 1;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

/* CapabilityKernel.Num: double.ToString(), i.e. the canonical number text. */
static const char *num(double v, char *buf)
{
    if (mm_format_double(v, buf, MM_FORMAT_DOUBLE_MAX) == 0) set_str(buf, MM_FORMAT_DOUBLE_MAX, "NaN");
    return buf;
}

static const char *num_opt(const mm_opt_double *v, char *buf)
{
    if (!v->present) { set_str(buf, MM_FORMAT_DOUBLE_MAX, "unset"); return buf; }
    return num(v->value, buf);
}

static const char *wire_time(int64_t t, char *buf)
{
    if (mm_time_format(t, buf, MM_TIME_TEXT_CAP) == 0) set_str(buf, MM_TIME_TEXT_CAP, "");
    return buf;
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

const char *mm_refusal_reason_wire(int reason)
{
    switch (reason) {
        case MM_REFUSAL_STOPPED: return "stopped";
        case MM_REFUSAL_UNKNOWN_CAPABILITY: return "unknown_capability";
        case MM_REFUSAL_CAPABILITY_UNAVAILABLE: return "capability_unavailable";
        case MM_REFUSAL_NO_CHARTER: return "no_charter";
        case MM_REFUSAL_LEASE_EXPIRED: return "lease_expired";
        case MM_REFUSAL_NOT_GRANTED: return "not_granted";
        case MM_REFUSAL_ROUTINE_NOT_REGISTERED: return "routine_not_registered";
        case MM_REFUSAL_ROUTINE_NOT_ENABLED: return "routine_not_enabled";
        case MM_REFUSAL_ACTION_CLASS_EXCEEDED: return "action_class_exceeded";
        case MM_REFUSAL_HAZARDOUS_PROHIBITED: return "hazardous_prohibited";
        case MM_REFUSAL_MISSING_PARAMETER: return "missing_parameter";
        case MM_REFUSAL_UNKNOWN_PARAMETER: return "unknown_parameter";
        case MM_REFUSAL_DUTY_CYCLE: return "duty_cycle";
        case MM_REFUSAL_RATE_LIMIT: return "rate_limit";
        case MM_REFUSAL_EXECUTOR_MISSING: return "executor_missing";
        case MM_REFUSAL_DRIVER_FAULT: return "driver_fault";
        case MM_REFUSAL_NO_RECORD_CAPACITY: return "no_record_capacity";
        default: return "refused";
    }
}

const char *mm_action_class_wire(int action_class)
{
    switch (action_class) {
        case 1: return "benign";
        case 2: return "controlled";
        case 3: return "hazardous";
        default: return "observe";
    }
}

int mm_capability_is_well_formed(const char *id)
{
    const char *p = id, *seg = id;
    int segments = 0;
    if (id == NULL || *id == '\0') return 0;
    for (;; p++) {
        if (*p == '.' || *p == '\0') {
            if (p == seg) return 0;                       /* empty segment */
            if (segments == 0) {
                size_t n = (size_t)(p - seg);
                if (!((n == 5 && strncmp(seg, "sense", 5) == 0) || (n == 3 && strncmp(seg, "act", 3) == 0) ||
                      (n == 7 && strncmp(seg, "routine", 7) == 0)))
                    return 0;
            }
            segments++;
            if (*p == '\0') break;
            seg = p + 1;
        } else if (!((*p >= 'a' && *p <= 'z') || (*p >= '0' && *p <= '9') || *p == '_')) {
            return 0;
        }
    }
    return segments >= 2;
}

/* ---- limits (LimitClamp) ---------------------------------------------------------------- */

static mm_opt_double min_of(mm_opt_double a, mm_opt_double b)
{
    if (!a.present) return b;
    if (!b.present) return a;
    a.value = a.value < b.value ? a.value : b.value;
    return a;
}

static mm_opt_double max_of(mm_opt_double a, mm_opt_double b)
{
    if (!a.present) return b;
    if (!b.present) return a;
    a.value = a.value > b.value ? a.value : b.value;
    return a;
}

void mm_limits_intersect(const mm_capability_limits *inner, const mm_capability_limits *outer, mm_capability_limits *out)
{
    mm_capability_limits r;
    r.max_on_s = min_of(inner->max_on_s, outer->max_on_s);
    r.min_off_s = max_of(inner->min_off_s, outer->min_off_s);
    r.min = max_of(inner->min, outer->min);
    r.max = min_of(inner->max, outer->max);
    r.max_rate_per_h = min_of(inner->max_rate_per_h, outer->max_rate_per_h);
    *out = r;
}

static int widens(mm_opt_double inner, mm_opt_double outer, int higher_is_wider)
{
    if (!inner.present || !outer.present) return 0;
    return higher_is_wider ? outer.value > inner.value : outer.value < inner.value;
}

int mm_limits_attempts_to_widen(const mm_capability_limits *inner, const mm_capability_limits *outer)
{
    return widens(inner->max_on_s, outer->max_on_s, 1) || widens(inner->max, outer->max, 1) ||
           widens(inner->max_rate_per_h, outer->max_rate_per_h, 1) ||
           widens(inner->min_off_s, outer->min_off_s, 0) || widens(inner->min, outer->min, 0);
}

/* ---- authority ---------------------------------------------------------------------------- */

const char *mm_authority_state(const mm_authority *a)
{
    if (a->stopped) return "stopped";
    if (!a->has_charter) return "observe_only";
    if (a->quiesced) return "quiesced";
    return "chartered";
}

int mm_authority_lease_alive(const mm_authority *a, int64_t now)
{
    return a->has_charter && !a->quiesced && !a->stopped && now < a->lease_expires_at;
}

int mm_authority_effective_ceiling(const mm_authority *a, int64_t now)
{
    int ceiling;
    if (!a->has_charter) return 0;
    if (!mm_authority_lease_alive(a, now)) return 0;
    ceiling = mm_action_class_parse(a->charter.action_ceiling);
    if (ceiling < 0) return 0;
    return ceiling == 3 ? 0 : ceiling;
}

void mm_authority_renew_lease(mm_authority *a, int64_t now)
{
    if (a->has_charter && !a->stopped && !a->quiesced) a->lease_expires_at = now + a->charter.lease_ttl_s;
}

int mm_authority_quiesce_if_expired(mm_authority *a, int64_t now)
{
    if (!a->has_charter || a->quiesced || a->stopped || now < a->lease_expires_at) return 0;
    a->quiesced = 1;
    return 1;
}

void mm_authority_stop(mm_authority *a)
{
    a->stopped = 1;
    a->has_charter = 0;
    a->quiesced = 0;
    a->lease_expires_at = INT64_MIN;
}

void mm_authority_clear_stop(mm_authority *a)
{
    a->stopped = 0;
}

static const mm_capability_limits *charter_limits_for(const mm_authority *a, const char *id)
{
    size_t i;
    if (!a->has_charter) return NULL;
    for (i = 0; i < a->charter.n_limits; i++)
        if (strcmp(a->charter.limits[i].capability, id) == 0) return &a->charter.limits[i].limits;
    return NULL;
}

static const mm_capability_limits *device_limits_for(const mm_authority *a, const char *id)
{
    size_t i;
    for (i = 0; i < a->n_device_limits; i++)
        if (strcmp(a->device_limits[i].id, id) == 0) return &a->device_limits[i].limits;
    return NULL;
}

/* EvidenceGate.RequiresEvidence(Authority.EffectiveEvidencePolicy(), id): the charter's patterns, or none. */
static int requires_evidence(const mm_authority *a, const char *id)
{
    size_t i;
    if (!a->has_charter) return 0;
    for (i = 0; i < a->charter.n_evidence_required_for; i++)
        if (mm_capability_pattern_matches(a->charter.evidence_required_for[i], id)) return 1;
    return 0;
}

/* ---- history ------------------------------------------------------------------------------ */

static mm_history_entry *history_find(mm_history *h, const char *key, int create)
{
    size_t i;
    for (i = 0; i < h->n_entries; i++)
        if (strcmp(h->entries[i].key, key) == 0) return &h->entries[i];
    if (!create || h->n_entries >= MM_MAX_HISTORY_KEYS) return NULL;
    memset(&h->entries[h->n_entries], 0, sizeof h->entries[0]);
    h->entries[h->n_entries].key = key;      /* keys are the compiled tables' ids: static lifetime */
    return &h->entries[h->n_entries++];
}

static int history_starts_in_trailing_hour(const mm_history *h, const char *key, int64_t now)
{
    size_t i, k;
    int count = 0;
    for (i = 0; i < h->n_entries; i++) {
        if (strcmp(h->entries[i].key, key) != 0) continue;
        for (k = 0; k < h->entries[i].n_starts; k++)
            if (h->entries[i].starts[k] > now - 3600) count++;
    }
    return count;
}

static void history_record(mm_history *h, const char *key, int64_t started_at, int64_t ended_at)
{
    mm_history_entry *e = history_find(h, key, 1);
    size_t i, kept = 0;
    if (!e) return;
    e->has_last_end = 1;
    e->last_end = ended_at;
    /* prune what the widest window can no longer see (as Record does, once there is more than one) */
    if (e->n_starts >= MM_HISTORY_STARTS) { memmove(e->starts, e->starts + 1, (MM_HISTORY_STARTS - 1) * sizeof e->starts[0]); e->n_starts--; }
    e->starts[e->n_starts++] = started_at;
    if (e->n_starts > 1) {
        for (i = 0; i < e->n_starts; i++)
            if (e->starts[i] > started_at - 3600) e->starts[kept++] = e->starts[i];
        e->n_starts = kept;
    }
}

/* ---- registries ---------------------------------------------------------------------------- */

static const mm_capability_desc *find_cap(const mm_kernel *k, const char *id, size_t *index)
{
    size_t i;
    for (i = 0; i < k->n_caps; i++)
        if (strcmp(k->caps[i].id, id) == 0) { if (index) *index = i; return &k->caps[i]; }
    return NULL;
}

static const mm_routine_desc *find_routine(const mm_kernel *k, const char *id, size_t *index)
{
    size_t i;
    for (i = 0; i < k->n_routines; i++)
        if (strcmp(k->routines[i].id, id) == 0) { if (index) *index = i; return &k->routines[i]; }
    return NULL;
}

static mm_executor *find_executor(const mm_kernel *k, const char *id)
{
    size_t i;
    for (i = 0; i < k->n_executors; i++)
        if (strcmp(k->executors[i]->capability_id, id) == 0) return k->executors[i];
    return NULL;
}

static int check_parameter_names(const char *id, const char *const *params, size_t n_params,
                                 const char *const *required, size_t n_required,
                                 const mm_param_range *ranges, size_t n_ranges,
                                 const char *duration, const char *magnitude, char *error, size_t cap)
{
    size_t i;
    for (i = 0; i < n_required; i++)
        if (!in_list(required[i], params, n_params)) {
            snprintf(error, cap, "'%s': required parameter '%s' is not an accepted parameter", id, required[i]);
            return -1;
        }
    for (i = 0; i < n_ranges; i++)
        if (!in_list(ranges[i].name, params, n_params)) {
            snprintf(error, cap, "'%s': parameter range declared for unknown parameter '%s'", id, ranges[i].name);
            return -1;
        }
    if (duration && !in_list(duration, params, n_params)) {
        snprintf(error, cap, "'%s': duration parameter '%s' is not an accepted parameter", id, duration);
        return -1;
    }
    if (magnitude && !in_list(magnitude, params, n_params)) {
        snprintf(error, cap, "'%s': magnitude parameter '%s' is not an accepted parameter", id, magnitude);
        return -1;
    }
    return 0;
}

int mm_kernel_init(mm_kernel *k, const char *mound_id,
                   const mm_capability_desc *caps, size_t n_caps,
                   const mm_routine_desc *routines, size_t n_routines,
                   char *error, size_t cap)
{
    size_t i, j;

    memset(k, 0, sizeof *k);
    if (cap) error[0] = '\0';
    if (n_caps > MM_MAX_CAPABILITY_DESCS || n_routines > MM_MAX_ROUTINE_DESCS) {
        snprintf(error, cap, "too many descriptors for this build (%d capabilities, %d routines)", MM_MAX_CAPABILITY_DESCS, MM_MAX_ROUTINE_DESCS);
        return -1;
    }

    /* CapabilityRegistry.Validate */
    for (i = 0; i < n_caps; i++) {
        const mm_capability_desc *d = &caps[i];
        if (!mm_capability_is_well_formed(d->id)) { snprintf(error, cap, "capability id '%s' is not well formed", d->id); return -1; }
        if (strncmp(d->id, "sense.", 6) == 0 && d->action_class != 0) {
            snprintf(error, cap, "'%s' is in the sense namespace and must be class 'observe'", d->id); return -1;
        }
        if (strncmp(d->id, "act.", 4) == 0 && d->action_class == 0) {
            snprintf(error, cap, "'%s' is in the act namespace and must declare a class above 'observe'", d->id); return -1;
        }
        if (d->action_class == 3) {
            snprintf(error, cap, "'%s' cannot be registered as 'hazardous': hazardous work has no authorization pipeline yet", d->id); return -1;
        }
        if (mm_capability_is_routine(d->id)) { snprintf(error, cap, "'%s' is a routine id and belongs in the routine registry", d->id); return -1; }
        if (check_parameter_names(d->id, d->parameters, d->n_parameters, d->required_parameters, d->n_required_parameters,
                                  d->ranges, d->n_ranges, d->duration_parameter, d->magnitude_parameter, error, cap) != 0)
            return -1;
        for (j = 0; j < i; j++)
            if (strcmp(caps[j].id, d->id) == 0) { snprintf(error, cap, "capability '%s' is declared twice", d->id); return -1; }
    }

    /* RoutineRegistry.Register */
    for (i = 0; i < n_routines; i++) {
        const mm_routine_desc *r = &routines[i];
        if (!mm_capability_is_routine(r->id)) { snprintf(error, cap, "routine id '%s' must be in the 'routine.' namespace", r->id); return -1; }
        if (!mm_capability_is_well_formed(r->id)) { snprintf(error, cap, "routine id '%s' is not well formed", r->id); return -1; }
        if (r->action_class == 3) {
            snprintf(error, cap, "'%s' cannot be registered as 'hazardous': hazardous work has no authorization pipeline yet", r->id); return -1;
        }
        if (r->n_required_capabilities == 0) { snprintf(error, cap, "'%s' drives no capabilities", r->id); return -1; }
        if (r->n_required_capabilities > MM_MAX_ROUTINE_BACKINGS) {
            snprintf(error, cap, "'%s' drives more than %d capabilities", r->id, MM_MAX_ROUTINE_BACKINGS); return -1;
        }
        for (j = 0; j < r->n_required_capabilities; j++) {
            const mm_capability_desc *backing = NULL;
            size_t bi;
            for (bi = 0; bi < n_caps; bi++) if (strcmp(caps[bi].id, r->required_capabilities[j]) == 0) backing = &caps[bi];
            if (!backing) { snprintf(error, cap, "'%s' requires capability '%s', which is not registered", r->id, r->required_capabilities[j]); return -1; }
            if (backing->action_class > r->action_class) {
                snprintf(error, cap, "'%s' is class '%s' but drives '%s', which is class '%s'", r->id,
                         mm_action_class_wire(r->action_class), backing->id, mm_action_class_wire(backing->action_class));
                return -1;
            }
        }
        if (check_parameter_names(r->id, r->parameters, r->n_parameters, r->required_parameters, r->n_required_parameters,
                                  r->ranges, r->n_ranges, r->duration_parameter, r->magnitude_parameter, error, cap) != 0)
            return -1;
    }

    k->caps = caps; k->n_caps = n_caps;
    k->routines = routines; k->n_routines = n_routines;
    for (i = 0; i < n_caps; i++) k->cap_available[i] = 1;
    for (i = 0; i < n_routines; i++) k->routine_available[i] = 1;
    set_str(k->authority.mound_id, sizeof k->authority.mound_id, mound_id);
    set_str(k->authority.safe_state, sizeof k->authority.safe_state, "all_actuators_off");
    k->authority.lease_expires_at = INT64_MIN;
    return 0;
}

void mm_kernel_apply_device_limits(mm_kernel *k, const mm_device_limit *limits, size_t n, const char *safe_state)
{
    size_t i;
    k->authority.n_device_limits = 0;
    for (i = 0; i < n && i < MM_MAX_LIMITS; i++) k->authority.device_limits[k->authority.n_device_limits++] = limits[i];
    if (safe_state && safe_state[0]) set_str(k->authority.safe_state, sizeof k->authority.safe_state, safe_state);
}

int mm_kernel_bind_executor(mm_kernel *k, mm_executor *executor)
{
    size_t i;
    if (!find_cap(k, executor->capability_id, NULL) && !find_routine(k, executor->capability_id, NULL)) return -1;
    for (i = 0; i < k->n_executors; i++)
        if (strcmp(k->executors[i]->capability_id, executor->capability_id) == 0) { k->executors[i] = executor; return 0; }
    if (k->n_executors >= MM_MAX_HISTORY_KEYS) return -1;
    k->executors[k->n_executors++] = executor;
    return 0;
}

int mm_kernel_set_available(mm_kernel *k, const char *id, int available)
{
    size_t i;
    if (find_cap(k, id, &i)) { k->cap_available[i] = (unsigned char)(available != 0); return 0; }
    if (find_routine(k, id, &i)) { k->routine_available[i] = (unsigned char)(available != 0); return 0; }
    return -1;
}

/* ---- charters ------------------------------------------------------------------------------ */

int mm_kernel_accept_charter(mm_kernel *k, const mm_charter_in *charter, int64_t now, mm_refusal *why)
{
    const char *cap_ids[MM_MAX_CAPABILITY_DESCS];
    const char *routine_ids[MM_MAX_ROUTINE_DESCS];
    size_t i;

    for (i = 0; i < k->n_caps; i++) cap_ids[i] = k->caps[i].id;
    for (i = 0; i < k->n_routines; i++) routine_ids[i] = k->routines[i].id;

    if (mm_charter_validate(charter, k->authority.mound_id, now, cap_ids, k->n_caps, routine_ids, k->n_routines, why) != 0)
        return why->count;

    /* A stop order outranks a charter: accepting one while stopped would let paperwork clear a stop. */
    if (k->authority.stopped) {
        memset(why, 0, sizeof *why);
        why->count = 1;
        set_str(why->reasons[0], MM_REASON_CAP, "mound is stopped; a stop must be explicitly cleared before a charter is accepted");
        return 1;
    }

    k->authority.charter = *charter;               /* complete replacement, never a diff */
    k->authority.has_charter = 1;
    k->authority.lease_expires_at = now + charter->lease_ttl_s;
    k->authority.quiesced = 0;                     /* a fresh charter is the only way out of quiesce */
    set_str(k->authority.safe_state, sizeof k->authority.safe_state, charter->safe_state);
    return 0;
}

int mm_kernel_review_charter(const mm_kernel *k, const mm_charter_in *charter, char *out, size_t cap)
{
    size_t i, len = 0;
    int notes = 0;
    if (cap) out[0] = '\0';

    for (i = 0; i < charter->n_limits; i++) {
        const char *id = charter->limits[i].capability;
        const mm_capability_limits *hardware = NULL, *device;
        const mm_capability_desc *c = find_cap(k, id, NULL);
        const mm_routine_desc *r = c ? NULL : find_routine(k, id, NULL);
        char note[MM_REASON_CAP];
        int have = 0;

        if (c) hardware = &c->hardware;
        else if (r) hardware = &r->hardware;

        if (hardware && mm_limits_attempts_to_widen(hardware, &charter->limits[i].limits)) {
            snprintf(note, sizeof note, "charter limits for '%s' try to widen the hardware bound; the hardware bound stands", id);
            have = 1;
        }
        if (have) {
            size_t n = strlen(note);
            if (notes && len + 2 < cap) { memcpy(out + len, "; ", 2); len += 2; }
            if (len + n < cap) { memcpy(out + len, note, n); len += n; }
            out[len < cap ? len : cap - 1] = '\0';
            notes++;
            have = 0;
        }

        device = device_limits_for(&k->authority, id);
        if (device && mm_limits_attempts_to_widen(device, &charter->limits[i].limits)) {
            snprintf(note, sizeof note, "charter limits for '%s' try to widen the configured device bound; the device bound stands", id);
            have = 1;
        }
        if (have) {
            size_t n = strlen(note);
            if (notes && len + 2 < cap) { memcpy(out + len, "; ", 2); len += 2; }
            if (len + n < cap) { memcpy(out + len, note, n); len += n; }
            out[len < cap ? len : cap - 1] = '\0';
            notes++;
        }
    }
    return notes;
}

/* ---- authorization ------------------------------------------------------------------------- */

/* What a request resolves to: a capability or a routine, with the fields Authorize reads. */
typedef struct resolved {
    const char *id;
    int is_routine;
    int action_class;
    mm_capability_limits hardware;
    const char *const *parameters;          size_t n_parameters;
    const char *const *required_parameters; size_t n_required_parameters;
    const mm_param_range *ranges;           size_t n_ranges;
    const char *duration_parameter;
    const char *magnitude_parameter;
    const char *history_keys[1 + MM_MAX_ROUTINE_BACKINGS];
    size_t n_history_keys;
} resolved;

/* KernelDecision.Refuse: the reason and detail, and nothing computed along the way. */
static void refuse(mm_decision *d, int reason, const char *detail)
{
    d->authorized = 0;
    d->refusal = reason;
    set_str(d->detail, sizeof d->detail, detail);
    d->n_effective = 0;
    d->clamped = 0;
    memset(&d->effective_limits, 0, sizeof d->effective_limits);
}

static int resolve(const mm_kernel *k, const char *id, resolved *t, mm_decision *d)
{
    char detail[MM_KERNEL_DETAIL_CAP];
    size_t index, i;

    memset(t, 0, sizeof *t);
    if (mm_capability_is_routine(id)) {
        const mm_routine_desc *r = find_routine(k, id, &index);
        if (!r) {
            snprintf(detail, sizeof detail, "no routine '%s' is registered in this build", id);
            refuse(d, MM_REFUSAL_ROUTINE_NOT_REGISTERED, detail);
            return -1;
        }
        if (!k->routine_available[index]) {
            snprintf(detail, sizeof detail, "routine '%s' reports itself unavailable", id);
            refuse(d, MM_REFUSAL_CAPABILITY_UNAVAILABLE, detail);
            return -1;
        }
        t->id = r->id; t->is_routine = 1; t->action_class = r->action_class;
        t->hardware = r->hardware;
        t->history_keys[t->n_history_keys++] = r->id;
        for (i = 0; i < r->n_required_capabilities; i++) {
            size_t bi;
            const mm_capability_desc *backing = find_cap(k, r->required_capabilities[i], &bi);
            if (!backing) {
                snprintf(detail, sizeof detail, "routine '%s' drives '%s', which is not registered", id, r->required_capabilities[i]);
                refuse(d, MM_REFUSAL_UNKNOWN_CAPABILITY, detail);
                return -1;
            }
            if (!k->cap_available[bi]) {
                snprintf(detail, sizeof detail, "routine '%s' drives '%s', which reports itself unavailable", id, r->required_capabilities[i]);
                refuse(d, MM_REFUSAL_CAPABILITY_UNAVAILABLE, detail);
                return -1;
            }
            /* a routine is only as permissive as the capabilities it drives */
            mm_limits_intersect(&backing->hardware, &t->hardware, &t->hardware);
            t->history_keys[t->n_history_keys++] = backing->id;
        }
        t->parameters = r->parameters; t->n_parameters = r->n_parameters;
        t->required_parameters = r->required_parameters; t->n_required_parameters = r->n_required_parameters;
        t->ranges = r->ranges; t->n_ranges = r->n_ranges;
        t->duration_parameter = r->duration_parameter; t->magnitude_parameter = r->magnitude_parameter;
        return 0;
    }

    {
        const mm_capability_desc *c = find_cap(k, id, &index);
        if (!c) {
            snprintf(detail, sizeof detail, mm_capability_is_well_formed(id)
                     ? "'%s' is not a capability this mound registers" : "'%s' is not a well-formed capability id", id);
            refuse(d, MM_REFUSAL_UNKNOWN_CAPABILITY, detail);
            return -1;
        }
        if (!k->cap_available[index]) {
            snprintf(detail, sizeof detail, "'%s' reports itself unavailable", id);
            refuse(d, MM_REFUSAL_CAPABILITY_UNAVAILABLE, detail);
            return -1;
        }
        t->id = c->id; t->is_routine = 0; t->action_class = c->action_class;
        t->hardware = c->hardware;
        t->parameters = c->parameters; t->n_parameters = c->n_parameters;
        t->required_parameters = c->required_parameters; t->n_required_parameters = c->n_required_parameters;
        t->ranges = c->ranges; t->n_ranges = c->n_ranges;
        t->duration_parameter = c->duration_parameter; t->magnitude_parameter = c->magnitude_parameter;
        t->history_keys[t->n_history_keys++] = c->id;
        return 0;
    }
}

static const mm_param_range *range_for(const resolved *t, const char *name)
{
    size_t i;
    for (i = 0; i < t->n_ranges; i++) if (strcmp(t->ranges[i].name, name) == 0) return &t->ranges[i];
    return NULL;
}

/* "<wire reason>: <detail>", bounded. */
static void prefixed(char *dst, size_t cap, const char *reason, const char *text)
{
    set_str(dst, cap, reason);
    if (strlen(dst) + 2 < cap) { strcat(dst, ": "); }
    {
        size_t len = strlen(dst), n = strlen(text);
        if (len + n >= cap) n = cap - 1 - len;
        memcpy(dst + len, text, n);
        dst[len + n] = '\0';
    }
}

/* Appends "; "-joined text into d->detail (bounded). */
static void append_note(char *detail, size_t cap, const char *note)
{
    size_t len = strlen(detail), n = strlen(note);
    if (len > 0) { if (len + 2 < cap) { memcpy(detail + len, "; ", 2); len += 2; detail[len] = '\0'; } else return; }
    if (len + n < cap) { memcpy(detail + len, note, n); detail[len + n] = '\0'; }
    else { memcpy(detail + len, note, cap - 1 - len); detail[cap - 1] = '\0'; }
}

void mm_kernel_authorize(mm_kernel *k, const mm_request *request, int64_t now, mm_decision *d)
{
    resolved t;
    mm_executor *bound;
    int ceiling;
    size_t i;
    char detail[MM_KERNEL_DETAIL_CAP], tm[MM_TIME_TEXT_CAP];
    char a[MM_FORMAT_DOUBLE_MAX], b[MM_FORMAT_DOUBLE_MAX], c[MM_FORMAT_DOUBLE_MAX], e[MM_FORMAT_DOUBLE_MAX];
    const mm_authority *auth = &k->authority;

    memset(d, 0, sizeof *d);
    d->refusal = MM_REFUSAL_NONE;

    /* 1. stop precedes all other work except observation; decided from the namespace */
    if (auth->stopped && strncmp(request->capability, "sense.", 6) != 0) {
        refuse(d, MM_REFUSAL_STOPPED, "a stop order is in force; stop precedes all work except observation");
        return;
    }

    /* 2 & 3 */
    if (resolve(k, request->capability, &t, d) != 0) return;

    bound = find_executor(k, t.id);
    if (bound && !bound->available) {
        snprintf(detail, sizeof detail, "'%s': driver reports itself unavailable", t.id);
        refuse(d, MM_REFUSAL_CAPABILITY_UNAVAILABLE, detail);
        return;
    }

    /* 4 */
    if (t.action_class == 3) {
        snprintf(detail, sizeof detail, "'%s' is hazardous class; hazardous work requires per-action authorization that does not exist yet", t.id);
        refuse(d, MM_REFUSAL_HAZARDOUS_PROHIBITED, detail);
        return;
    }

    /* 5 */
    ceiling = mm_authority_effective_ceiling(auth, now);
    if (t.action_class > ceiling) {
        if (!auth->has_charter) {
            snprintf(detail, sizeof detail, "no charter: authority is 'observe' only, '%s' is class '%s'", t.id, mm_action_class_wire(t.action_class));
            refuse(d, MM_REFUSAL_NO_CHARTER, detail);
            return;
        }
        if (!mm_authority_lease_alive(auth, now)) {
            snprintf(detail, sizeof detail, "lease expired at %s; awaiting a fresh charter", wire_time(auth->lease_expires_at, tm));
            refuse(d, MM_REFUSAL_LEASE_EXPIRED, detail);
            return;
        }
        snprintf(detail, sizeof detail, "'%s' is class '%s', charter ceiling is '%s'", t.id,
                 mm_action_class_wire(t.action_class), mm_action_class_wire(ceiling));
        refuse(d, MM_REFUSAL_ACTION_CLASS_EXCEEDED, detail);
        return;
    }

    /* 6 */
    if (auth->has_charter) {
        int granted = t.is_routine
            ? in_table(t.id, auth->charter.routines[0], MM_NAME_CAP, auth->charter.n_routines)
            : in_table(t.id, auth->charter.capabilities[0], MM_NAME_CAP, auth->charter.n_capabilities);
        if (!granted) {
            snprintf(detail, sizeof detail, "charter '%s' does not %s'%s'", auth->charter.charter_id,
                     t.is_routine ? "enable routine " : "grant capability ", t.id);
            refuse(d, t.is_routine ? MM_REFUSAL_ROUTINE_NOT_ENABLED : MM_REFUSAL_NOT_GRANTED, detail);
            return;
        }
    } else if (t.action_class > 0) {
        snprintf(detail, sizeof detail, "no charter: '%s' is not observe class", t.id);
        refuse(d, MM_REFUSAL_NO_CHARTER, detail);
        return;
    }

    /* 7 */
    if (request->worker_ceiling >= 0 && t.action_class > request->worker_ceiling) {
        snprintf(detail, sizeof detail, "worker '%s' has ceiling '%s', '%s' is class '%s'", request->worker ? request->worker : "",
                 mm_action_class_wire(request->worker_ceiling), t.id, mm_action_class_wire(t.action_class));
        refuse(d, MM_REFUSAL_ACTION_CLASS_EXCEEDED, detail);
        return;
    }

    /* 8 */
    for (i = 0; i < request->n_parameters; i++)
        if (!in_list(request->parameters[i].key, t.parameters, t.n_parameters)) {
            snprintf(detail, sizeof detail, "'%s' does not accept parameter '%s'", t.id, request->parameters[i].key);
            refuse(d, MM_REFUSAL_UNKNOWN_PARAMETER, detail);
            return;
        }
    for (i = 0; i < t.n_required_parameters; i++) {
        size_t j;
        int present = 0;
        for (j = 0; j < request->n_parameters; j++) if (strcmp(request->parameters[j].key, t.required_parameters[i]) == 0) present = 1;
        if (!present) {
            snprintf(detail, sizeof detail, "'%s' requires parameter '%s'", t.id, t.required_parameters[i]);
            refuse(d, MM_REFUSAL_MISSING_PARAMETER, detail);
            return;
        }
    }

    /* 9 */
    {
        static const mm_capability_limits none = { {0, 0}, {0, 0}, {0, 0}, {0, 0}, {0, 0} };
        const mm_capability_limits *device = device_limits_for(auth, t.id);
        const mm_capability_limits *charter = charter_limits_for(auth, t.id);
        mm_limits_intersect(&t.hardware, device ? device : &none, &d->effective_limits);
        mm_limits_intersect(&d->effective_limits, charter ? charter : &none, &d->effective_limits);
    }

    /* 10 & 11: across every capability the request will move */
    if (t.action_class > 0) {
        for (i = 0; i < t.n_history_keys; i++) {
            const mm_history_entry *e = history_find(&k->history, t.history_keys[i], 0);
            if (d->effective_limits.min_off_s.present && e && e->has_last_end &&
                (double)now < (double)e->last_end + d->effective_limits.min_off_s.value) {
                snprintf(detail, sizeof detail, "'%s': min_off_s %s not elapsed since %s", t.history_keys[i],
                         num(d->effective_limits.min_off_s.value, a), wire_time(e->last_end, tm));
                refuse(d, MM_REFUSAL_DUTY_CYCLE, detail);
                return;
            }
            if (d->effective_limits.max_rate_per_h.present &&
                (double)history_starts_in_trailing_hour(&k->history, t.history_keys[i], now) >= d->effective_limits.max_rate_per_h.value) {
                snprintf(detail, sizeof detail, "'%s': max_rate_per_h %s already reached in the trailing hour", t.history_keys[i],
                         num(d->effective_limits.max_rate_per_h.value, a));
                refuse(d, MM_REFUSAL_RATE_LIMIT, detail);
                return;
            }
        }
    }

    /* 12: clamp, and say what narrowed */
    d->detail[0] = '\0';
    for (i = 0; i < request->n_parameters && i < MM_MAX_PARAMS; i++) {
        const char *name = request->parameters[i].key;
        double allowed = request->parameters[i].value;
        const mm_param_range *range = range_for(&t, name);
        char note[MM_REASON_CAP];

        if (range && !(allowed >= range->min && allowed <= range->max)) {
            double narrowed = allowed < range->min ? range->min : allowed > range->max ? range->max : allowed;
            snprintf(note, sizeof note, "'%s' %s -> %s by driver range [%s, %s]", name, num(allowed, a), num(narrowed, b),
                     num(range->min, c), num(range->max, e));
            append_note(d->detail, sizeof d->detail, note);
            allowed = narrowed;
        }
        if (t.duration_parameter && strcmp(name, t.duration_parameter) == 0 &&
            d->effective_limits.max_on_s.present && allowed > d->effective_limits.max_on_s.value) {
            snprintf(note, sizeof note, "'%s' %s -> %s by max_on_s %s", name, num(allowed, a),
                     num(d->effective_limits.max_on_s.value, b), num(d->effective_limits.max_on_s.value, c));
            append_note(d->detail, sizeof d->detail, note);
            allowed = d->effective_limits.max_on_s.value;
        }
        if (t.magnitude_parameter && strcmp(name, t.magnitude_parameter) == 0) {
            double by = allowed;
            if (d->effective_limits.min.present && by < d->effective_limits.min.value) by = d->effective_limits.min.value;
            if (d->effective_limits.max.present && by > d->effective_limits.max.value) by = d->effective_limits.max.value;
            if (by != allowed) {
                snprintf(note, sizeof note, "'%s' %s -> %s by [%s, %s]", name, num(allowed, a), num(by, b),
                         num_opt(&d->effective_limits.min, c), num_opt(&d->effective_limits.max, e));
                append_note(d->detail, sizeof d->detail, note);
                allowed = by;
            }
        }
        d->effective[d->n_effective].key = name;
        d->effective[d->n_effective].value = allowed;
        d->n_effective++;
    }
    d->clamped = d->detail[0] != '\0';

    /* 13 */
    if (!bound) {
        snprintf(detail, sizeof detail, "'%s' is authorized but no executor is bound to it", t.id);
        refuse(d, MM_REFUSAL_EXECUTOR_MISSING, detail);
        return;
    }

    /*
     * 14. And the mound has to be able to SAY that it did it.
     *
     * The uplink queue is bounded, and a bound enforced after the effect trades history the mound
     * already owes for work it has not done yet. Refusing loses only the latter, and the controller
     * can ask again. LAST, so a refusal an operator could act on is never masked by this one; and
     * observation is exempt for the same reason a stop does not blind the mound — a reading that
     * cannot be queued is a lost reading, while an actuation that cannot be queued is a physical
     * change nobody can account for.
     */
    if (t.action_class > 0 && k->audit_capacity > 0 &&
        k->audit_pending >= k->audit_capacity) {
        snprintf(detail, sizeof detail,
                 "the audit path is full (%d of %d records pending); "
                 "a mound that cannot record what it did must not do it",
                 k->audit_pending, k->audit_capacity);
        refuse(d, MM_REFUSAL_NO_RECORD_CAPACITY, detail);
        return;
    }

    d->authorized = 1;
    d->required_class = t.action_class;
    d->evidence_required = requires_evidence(auth, t.id);
    if (t.duration_parameter)
        for (i = 0; i < d->n_effective; i++)
            if (strcmp(d->effective[i].key, t.duration_parameter) == 0) { d->has_duration = 1; d->duration_s = d->effective[i].value; }
    for (i = 0; i < t.n_history_keys; i++) d->history_keys[i] = t.history_keys[i];
    d->n_history_keys = t.n_history_keys;
}

/* ---- execution ----------------------------------------------------------------------------- */

/* EvidenceGate.Gate over the evidence the executor produced. Returns the (possibly demoted) outcome. */
static const char *gate(const mm_kernel *k, const mm_action_record_in *record, const mm_outcome *outcome,
                        int64_t now, char *reason, size_t reason_cap)
{
    int64_t started, oldest;
    long long min_interval;
    int required;
    size_t i;

    reason[0] = '\0';
    if (strcmp(record->outcome, "succeeded") != 0 && strcmp(record->outcome, "clamped") != 0) return record->outcome;

    if (record->n_evidence_refs == 0) {
        set_str(reason, reason_cap, "no evidence referenced");
        return "unverified";
    }

    if (mm_time_parse(record->started_at, &started) != 0) started = now;
    required = requires_evidence(&k->authority, record->capability);
    min_interval = k->authority.has_charter ? k->authority.charter.evidence_min_interval_s : 60;
    oldest = started - (min_interval > 0 ? min_interval : 0);

    for (i = 0; i < record->n_evidence_refs; i++) {
        const mm_evidence_produced *item = NULL;
        int64_t captured;
        size_t j;
        char st[MM_TIME_TEXT_CAP], ct[MM_TIME_TEXT_CAP];
        for (j = 0; j < outcome->n_evidence; j++)
            if (strcmp(outcome->evidence[j].id, record->evidence_refs[i]) == 0) item = &outcome->evidence[j];
        if (!item) { snprintf(reason, reason_cap, "evidence '%s' is missing", record->evidence_refs[i]); return "unverified"; }
        if (mm_time_parse(item->captured_at, &captured) != 0) {
            snprintf(reason, reason_cap, "evidence '%s' has an unparseable captured_at", item->id); return "unverified";
        }
        if (captured > now) { snprintf(reason, reason_cap, "evidence '%s' is captured in the future", item->id); return "unverified"; }
        if (required && captured < oldest) {
            snprintf(reason, reason_cap, "evidence '%.63s' is stale (captured %.20s, action started %.20s, min_interval_s %lld)", item->id,
                     wire_time(captured, ct), wire_time(started, st), min_interval);   /* precisions: the fields' own caps, for gcc's truncation heuristic */
            return "unverified";
        }
    }
    return record->outcome;
}

void mm_kernel_execute(mm_kernel *k, const mm_request *request, int64_t now, const char *action_id,
                       mm_action_record_in *record)
{
    mm_decision d;
    mm_execution x;
    mm_outcome outcome;
    mm_executor *executor;
    int64_t ended;
    size_t i;
    char tm[MM_TIME_TEXT_CAP];

    mm_kernel_authorize(k, request, now, &d);

    /* NewRecord */
    memset(record, 0, sizeof *record);
    set_str(record->action_id, sizeof record->action_id, action_id);
    set_str(record->mission_id, sizeof record->mission_id, request->mission_id ? request->mission_id : "");
    set_str(record->charter_id, sizeof record->charter_id, k->authority.has_charter ? k->authority.charter.charter_id : "");
    set_str(record->capability, sizeof record->capability, request->capability);
    set_str(record->routine_id, sizeof record->routine_id, mm_capability_is_routine(request->capability) ? request->capability : "");
    for (i = 0; i < request->n_parameters && i < MM_MAX_PARAMS; i++) {
        set_str(record->requested_parameters[i].key, MM_NAME_CAP, request->parameters[i].key);
        record->requested_parameters[i].value = request->parameters[i].value;
    }
    record->n_requested_parameters = request->n_parameters < MM_MAX_PARAMS ? request->n_parameters : MM_MAX_PARAMS;
    for (i = 0; i < d.n_effective; i++) {
        set_str(record->parameters[i].key, MM_NAME_CAP, d.effective[i].key);
        record->parameters[i].value = d.effective[i].value;
    }
    record->n_parameters = d.n_effective;
    set_str(record->started_at, sizeof record->started_at, wire_time(now, tm));
    set_str(record->ended_at, sizeof record->ended_at, record->started_at);
    record->evidence_required = requires_evidence(&k->authority, request->capability);
    set_str(record->outcome, sizeof record->outcome, "unverified");

    if (!d.authorized) {
        set_str(record->outcome, sizeof record->outcome, d.refusal == MM_REFUSAL_STOPPED ? "stopped" : "refused");
        prefixed(record->detail, sizeof record->detail, mm_refusal_reason_wire(d.refusal), d.detail);
        record->n_parameters = 0;
        return;
    }

    executor = find_executor(k, request->capability);
    memset(&x, 0, sizeof x);
    x.capability_id = request->capability;
    x.routine_id = record->routine_id;
    x.parameters = d.effective;
    x.n_parameters = d.n_effective;
    x.started_at = now;
    x.effective_limits = d.effective_limits;
    x.mission_id = record->mission_id;

    memset(&outcome, 0, sizeof outcome);
    if (executor->run(executor->ctx, &x, &outcome) != 0) {
        /* the C# catch: a driver that throws must not take the runtime down, and must not vanish */
        outcome.succeeded = 0;
        outcome.n_evidence = 0;
        outcome.has_ended_at = 0;
        if (outcome.detail[0] == '\0') set_str(outcome.detail, sizeof outcome.detail, "executor failed");
    }

    ended = outcome.has_ended_at ? outcome.ended_at : now + (int64_t)(d.has_duration ? d.duration_s : 0);
    set_str(record->ended_at, sizeof record->ended_at, wire_time(ended, tm));

    /* hardware was touched either way, so the duty cycle applies either way */
    if (d.required_class > 0)
        for (i = 0; i < d.n_history_keys; i++) history_record(&k->history, d.history_keys[i], now, ended);

    if (!outcome.succeeded) {
        set_str(record->outcome, sizeof record->outcome, "failed");
        prefixed(record->detail, sizeof record->detail, mm_refusal_reason_wire(MM_REFUSAL_DRIVER_FAULT), outcome.detail);
        return;
    }

    set_str(record->outcome, sizeof record->outcome, d.clamped ? "clamped" : "succeeded");
    if (d.clamped) set_str(record->detail, sizeof record->detail, d.detail);

    for (i = 0; i < outcome.n_evidence && i < MM_MAX_EVIDENCE_IDS; i++) {
        set_str(record->evidence_refs[record->n_evidence_refs++], MM_ID_CAP, outcome.evidence[i].id);
        if (k->inline_evidence) {
            mm_evidence_item_in *item = &record->evidence[record->n_evidence++];
            set_str(item->evidence_id, sizeof item->evidence_id, outcome.evidence[i].id);
            set_str(item->type, sizeof item->type, outcome.evidence[i].type);
            set_str(item->captured_at, sizeof item->captured_at, outcome.evidence[i].captured_at);
            set_str(item->source, sizeof item->source, outcome.evidence[i].source);
            set_str(item->payload_json, sizeof item->payload_json, outcome.evidence[i].payload_json);
            item->content_digest[0] = '\0';
        }
    }

    {
        char reason[MM_REASON_CAP];
        const char *gated = gate(k, record, &outcome, now, reason, sizeof reason);
        if (strcmp(gated, record->outcome) != 0) {
            set_str(record->outcome, sizeof record->outcome, gated);
            if (record->detail[0] == '\0') set_str(record->detail, sizeof record->detail, reason);
            else append_note(record->detail, sizeof record->detail, reason);
        }
    }
}
