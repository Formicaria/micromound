/*
 * mm_kernel — the capability kernel in C: Micromound.Capabilities, decision for decision.
 *
 * The one authority boundary (SAFETY.md Layer 1–3, docs/CAPABILITIES.md), for a device whose
 * capability and routine tables are compiled in. Same check order as CapabilityKernel.Authorize:
 *
 *   1. stop precedes everything but observation (decided from the NAMESPACE, before anything resolves)
 *   2–3. resolve the capability or routine; refuse specifically when it does not resolve or is unavailable
 *   4. hazardous is never authorized (no per-action pipeline exists)
 *   5. authority: no charter / lease expired / class above the ceiling
 *   6. granted by the active charter (a charter is a complete replacement, never a diff)
 *   7. the requesting worker's own ceiling
 *   8. parameters: unknown refused (not dropped), required present
 *   9. the bound in force: hardware ∩ device ∩ charter
 *   10–11. duty cycle and rate, across every capability the request would move
 *   12. clamp, and say what narrowed
 *   13. something must be bound to do it
 *
 * Same closed set of refusal reasons, same detail text, same records (mm_action_record_in) as the
 * host — pinned by tests/Micromound.Tests/Golden/files/kernel-decisions.txt, which a C# test
 * writes and this kernel must reproduce line for line.
 *
 * Nothing here allocates. Tables are the caller's (typically `static const`); state is in the
 * mm_kernel struct. Time is int64 epoch seconds (mm_time).
 */
#ifndef MM_KERNEL_H
#define MM_KERNEL_H

#include <stddef.h>
#include <stdint.h>
#include "mm_bodies.h"
#include "mm_decode.h"

#ifdef __cplusplus
extern "C" {
#endif

#define MM_MAX_CAPABILITY_DESCS 16
#define MM_MAX_ROUTINE_DESCS 8
#define MM_MAX_ROUTINE_BACKINGS 4       /* capabilities one routine drives */
#define MM_MAX_HISTORY_KEYS (MM_MAX_CAPABILITY_DESCS + MM_MAX_ROUTINE_DESCS)
#define MM_HISTORY_STARTS 16            /* starts remembered per key; max_rate_per_h above this saturates */
#define MM_KERNEL_DETAIL_CAP MM_DETAIL_CAP

/* ---- compiled tables ---- */

typedef struct mm_param_range {
    const char *name;
    double min, max;
} mm_param_range;

/* CapabilityDescriptor. Class: 0 observe, 1 benign, 2 controlled (3 hazardous is refused at init). */
typedef struct mm_capability_desc {
    const char *id;
    int action_class;
    mm_capability_limits hardware;
    const char *const *parameters;          size_t n_parameters;
    const char *const *required_parameters; size_t n_required_parameters;
    const mm_param_range *ranges;           size_t n_ranges;
    const char *duration_parameter;         /* NULL when none */
    const char *magnitude_parameter;        /* NULL when none */
} mm_capability_desc;

/* RoutineDescriptor. */
typedef struct mm_routine_desc {
    const char *id;
    int action_class;
    const char *const *required_capabilities; size_t n_required_capabilities;
    mm_capability_limits hardware;
    const char *const *parameters;          size_t n_parameters;
    const char *const *required_parameters; size_t n_required_parameters;
    const mm_param_range *ranges;           size_t n_ranges;
    const char *duration_parameter;
    const char *magnitude_parameter;
} mm_routine_desc;

/* ---- executors ---- */

typedef struct mm_execution {
    const char *capability_id;
    const char *routine_id;                 /* "" unless the request named a routine */
    const mm_param *parameters;             /* effective, after clamping */
    size_t n_parameters;
    int64_t started_at;
    mm_capability_limits effective_limits;
    const char *mission_id;
} mm_execution;


/* EvidenceItem as an executor produces it: the gate reads id and captured_at; the rest rides along
   for whatever carries evidence off the device (PROTOCOL.md §8 leaves that open for a controller). */
typedef struct mm_evidence_produced {
    char id[MM_ID_CAP];
    char captured_at[MM_TIME_TEXT_CAP];
    char type[MM_NAME_CAP];             /* "reading" for a numeric reading */
    char source[MM_NAME_CAP];
    char payload_json[MM_PAYLOAD_CAP];
} mm_evidence_produced;

typedef struct mm_outcome {
    int succeeded;
    char detail[MM_DETAIL_CAP];
    int has_ended_at;
    int64_t ended_at;
    mm_evidence_produced evidence[MM_MAX_EVIDENCE_IDS];
    size_t n_evidence;
} mm_outcome;

/* Fills *out. A non-zero return is the C# "executor threw": recorded as a fault with out->detail. */
typedef int (*mm_executor_fn)(void *ctx, const mm_execution *x, mm_outcome *out);

typedef struct mm_executor {
    const char *capability_id;
    mm_executor_fn run;
    void *ctx;
    int available;                          /* ICapabilityExecutor.IsAvailable — the driver's own health */
} mm_executor;

/* ---- decisions ---- */

enum mm_refusal_reason {
    MM_REFUSAL_NONE = -1,
    MM_REFUSAL_STOPPED = 0,
    MM_REFUSAL_UNKNOWN_CAPABILITY,
    MM_REFUSAL_CAPABILITY_UNAVAILABLE,
    MM_REFUSAL_NO_CHARTER,
    MM_REFUSAL_LEASE_EXPIRED,
    MM_REFUSAL_NOT_GRANTED,
    MM_REFUSAL_ROUTINE_NOT_REGISTERED,
    MM_REFUSAL_ROUTINE_NOT_ENABLED,
    MM_REFUSAL_ACTION_CLASS_EXCEEDED,
    MM_REFUSAL_HAZARDOUS_PROHIBITED,
    MM_REFUSAL_MISSING_PARAMETER,
    MM_REFUSAL_UNKNOWN_PARAMETER,
    MM_REFUSAL_DUTY_CYCLE,
    MM_REFUSAL_RATE_LIMIT,
    MM_REFUSAL_EXECUTOR_MISSING,
    MM_REFUSAL_DRIVER_FAULT,
    MM_REFUSAL_NO_RECORD_CAPACITY
};

/* RefusalReasons.ToWire. */
const char *mm_refusal_reason_wire(int reason);

/* ActionClasses.ToWire; "observe" for anything unknown, as in C#. */
const char *mm_action_class_wire(int action_class);

typedef struct mm_request {
    const char *capability;
    const mm_param *parameters;
    size_t n_parameters;
    const char *mission_id;                 /* "" when none */
    const char *worker;                     /* "" when none */
    int worker_ceiling;                     /* -1 = no worker ceiling */
} mm_request;

typedef struct mm_decision {
    int authorized;
    int refusal;                            /* enum mm_refusal_reason */
    char detail[MM_KERNEL_DETAIL_CAP];
    mm_param effective[MM_MAX_PARAMS];      /* keys point into the request */
    size_t n_effective;
    int clamped;
    mm_capability_limits effective_limits;
    int required_class;
    int evidence_required;
    int has_duration;
    double duration_s;
    const char *history_keys[1 + MM_MAX_ROUTINE_BACKINGS];
    size_t n_history_keys;
} mm_decision;

/* ---- authority (KernelAuthority) ---- */

typedef struct mm_device_limit {
    char id[MM_NAME_CAP];
    mm_capability_limits limits;
} mm_device_limit;

typedef struct mm_authority {
    char mound_id[MM_ID_CAP];
    int has_charter;
    mm_charter_in charter;
    int64_t lease_expires_at;
    int stopped;
    int quiesced;
    char safe_state[MM_NAME_CAP];
    mm_device_limit device_limits[MM_MAX_LIMITS];
    size_t n_device_limits;
} mm_authority;

/* MoundStates: "stopped" | "observe_only" | "quiesced" | "chartered". */
const char *mm_authority_state(const mm_authority *a);
int mm_authority_lease_alive(const mm_authority *a, int64_t now);
int mm_authority_effective_ceiling(const mm_authority *a, int64_t now);
void mm_authority_renew_lease(mm_authority *a, int64_t now);
int mm_authority_quiesce_if_expired(mm_authority *a, int64_t now);   /* 1 when it quiesced now */
void mm_authority_stop(mm_authority *a);
void mm_authority_clear_stop(mm_authority *a);

/* ---- history (ActuationHistory) ---- */

typedef struct mm_history_entry {
    const char *key;
    int has_last_end;
    int64_t last_end;
    int64_t starts[MM_HISTORY_STARTS];
    size_t n_starts;
    /* Monotonic stamps taken when each instant above was recorded; 0 = none. See mm_history.monotonic_now. */
    int64_t last_end_mono;
    int64_t starts_mono[MM_HISTORY_STARTS];
} mm_history_entry;

typedef struct mm_history {
    mm_history_entry entries[MM_MAX_HISTORY_KEYS];
    size_t n_entries;
    /*
     * The current reading of a clock nobody can step (mm_hal.monotonic_s), or 0 for "none" —
     * ActuationHistory.Time on the host. When it is set, an entry's age is the SMALLER of what the
     * wall clock and this one claim, so a wall clock stepped FORWARD cannot hand back a cooldown or
     * refresh a rate budget (v0.9.38, roadmap P0.5). For "has enough time passed?" the smaller
     * answer is the safe one — the mirror image of the rule for releasing a hold, where the LARGER
     * elapsed wins. 0 is wall-clock only, which is what a bare mm_kernel and the golden fixtures get.
     * mm_app sets it from the HAL once per tick.
     */
    int64_t monotonic_now;
} mm_history;

/* ---- the kernel ---- */

typedef struct mm_kernel {
    const mm_capability_desc *caps;   size_t n_caps;
    const mm_routine_desc *routines;  size_t n_routines;
    unsigned char cap_available[MM_MAX_CAPABILITY_DESCS];        /* CapabilityDescriptor.Available */
    unsigned char routine_available[MM_MAX_ROUTINE_DESCS];       /* RoutineDescriptor.Available */
    mm_executor *executors[MM_MAX_HISTORY_KEYS];                 /* bound by capability_id */
    size_t n_executors;
    mm_authority authority;
    mm_history history;
    int inline_evidence;                    /* 1 on a reduced-profile device: the items an executor produced ride
                                               inline on the record (PROTOCOL.md §6). 0 mirrors the host kernel,
                                               whose items travel by evidence_bundle from the store. */
    /*
     * Check 14 (IAuditCapacity): how much room the uplink queue has left for the record a new
     * actuation would produce. The rule is exactly `audit_pending < audit_capacity`, over these two
     * integers and nothing else, so the C and C# kernels cannot drift on what "full" means — a bool
     * could differ between them with no fixture able to see it.
     *
     * audit_capacity <= 0 = no bound is in force and the check does not apply, which is what a bare
     * mm_kernel gets by default (mm_kernel_init zeroes both). mm_device sets them from its own queue
     * before every act, so a real board always has the check.
     */
    int audit_pending;
    int audit_capacity;
} mm_kernel;

/*
 * Validates the tables with the registries' rules (well-formed ids, sense is observe, act is above
 * observe, no hazardous, required ⊆ parameters, ranges/duration/magnitude name parameters, a
 * routine drives ≥ 1 registered capability of no higher class) and initialises the kernel.
 * Returns 0, or -1 with the first violation in error (the registry's message).
 */
int mm_kernel_init(mm_kernel *k, const char *mound_id,
                   const mm_capability_desc *caps, size_t n_caps,
                   const mm_routine_desc *routines, size_t n_routines,
                   char *error, size_t error_cap);

/* The manifest's device_limits tier and safe_state (KernelAuthority.ApplyManifest). */
void mm_kernel_apply_device_limits(mm_kernel *k, const mm_device_limit *limits, size_t n, const char *safe_state);

/* RegisterExecutor: the executor's id must name a capability or routine. 0, or -1. The executor must outlive the kernel. */
int mm_kernel_bind_executor(mm_kernel *k, mm_executor *executor);

/* CapabilityDescriptor/RoutineDescriptor.Available at runtime (a manifest disabled it). 0, or -1 for an unknown id. */
int mm_kernel_set_available(mm_kernel *k, const char *id, int available);

/*
 * KernelAuthority.AcceptCharter: CharterValidator with THIS device's tables as the registries,
 * then the stop rule. 0 = accepted (the charter is copied in; the lease starts now); the refusal
 * count otherwise, with the reasons in *why.
 */
int mm_kernel_accept_charter(mm_kernel *k, const mm_charter_in *charter, int64_t now, mm_refusal *why);

/* ReviewCharter: the widening notes (never a refusal), joined by "; " into out. Returns the note count. */
int mm_kernel_review_charter(const mm_kernel *k, const mm_charter_in *charter, char *out, size_t cap);

/* CapabilityKernel.Authorize. */
void mm_kernel_authorize(mm_kernel *k, const mm_request *request, int64_t now, mm_decision *out);

/*
 * CapabilityKernel.Execute: authorize, run the executor, record the duty cycle, gate the outcome
 * on evidence ("commands are not evidence"), and fill the action record. action_id is the
 * caller's (a UUID on a device; the golden test normalizes it).
 */
void mm_kernel_execute(mm_kernel *k, const mm_request *request, int64_t now, const char *action_id,
                       mm_action_record_in *record);

/* CapabilityId.IsWellFormed. */
int mm_capability_is_well_formed(const char *id);

/* LimitClamp.Intersect / Effective / AttemptsToWiden, exposed for tests and for callers that show bounds. */
void mm_limits_intersect(const mm_capability_limits *inner, const mm_capability_limits *outer, mm_capability_limits *out);
int mm_limits_attempts_to_widen(const mm_capability_limits *inner, const mm_capability_limits *outer);

#ifdef __cplusplus
}
#endif

#endif
