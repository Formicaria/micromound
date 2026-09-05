/*
 * mm_decode — the receive side of the reduced profile (PROTOCOL.md §2, §4, §7, §8).
 *
 * A device receives signed envelopes carrying `charter`, `stop` and `ack` bodies. This module
 * takes the wire bytes and produces fixed-size C structs, refusing anything outside the contract's
 * shape or capacity, then validates them with the SAME rules and the SAME closed set of refusal
 * reasons as Micromound.Protocol's EnvelopeValidator and CharterValidator — a controller that
 * refused differently from a Pi would make "the mound refused" mean two different things.
 *
 * Order of operations for a received envelope, as RecordAnts does it:
 *   1. mm_envelope_verify_wire  — the signature over the bytes AS RECEIVED (see below), and the digest
 *   2. mm_envelope_parse        — the frame, into an mm_envelope_in
 *   3. mm_envelope_validate     — shape and kind (reduced profile)
 *   4. mm_<kind>_parse          — the body, into its struct; then mm_charter_validate for a charter
 *
 * Verification works on the bytes as received because a signed envelope on the wire IS its
 * canonical bytes with the signature spliced into the last field (`sig` is last by declaration
 * order, and every emitter serializes through the same options). A sender that re-serialized
 * non-canonically would fail verification here and be refused — the fail-closed direction.
 *
 * Capacities are compile-time constants sized for the protocol's identifiers (UUIDs, "mm-" ids,
 * "act.relay_1"-style capabilities). Exceeding one is a refusal (MM_JR_OVERFLOW / MM_JR_TOO_MANY),
 * never a truncation.
 */
#ifndef MM_DECODE_H
#define MM_DECODE_H

#include <stddef.h>
#include <stdint.h>
#include "mm_bodies.h"
#include "mm_envelope.h"
#include "mm_time.h"

#ifdef __cplusplus
extern "C" {
#endif

#define MM_ID_CAP 64                /* UUID 36; "mm-<uuid>" 39 */
#define MM_KIND_CAP 24
#define MM_NAME_CAP 48              /* capability, routine, state and ceiling names */
#define MM_DETAIL_CAP 320           /* a clamp note plus an evidence-gate reason fits */
#define MM_MAX_CAPABILITIES 16
#define MM_MAX_ROUTINES 8
#define MM_MAX_LIMITS 16
#define MM_MAX_REQUIRED_FOR 8
#define MM_MAX_EVIDENCE_IDS 16
#define MM_MAX_PARAMS 8

/* ---- the envelope frame ---- */

typedef struct mm_envelope_in {
    long long v;
    char id[MM_ID_CAP];
    char mound_id[MM_ID_CAP];
    long long seq;
    char sent_at[MM_TIME_TEXT_CAP];
    char kind[MM_KIND_CAP];
    const char *body;               /* the body's exact source bytes, inside the input */
    size_t body_len;
    char prev_digest[MM_DIGEST_TEXT_LEN + 1];
    char sig[MM_SIG_TEXT_LEN + 1];
} mm_envelope_in;

/* Parses the frame. Returns 0, or -1 with *error = an mm_jr_error. Unknown members are skipped. */
int mm_envelope_parse(const char *json, size_t n, mm_envelope_in *out, int *error);

/*
 * Verifies the signature of a wire envelope over its canonical bytes, in place, and computes the
 * digest the next envelope's prev_digest must carry (digest_out may be NULL). 0 = verified;
 * -1 = the tail is not `,"sig":"ed25519:<128 hex>"}`, or the signature does not verify under pk.
 */
int mm_envelope_verify_wire(const char *wire, size_t n, const uint8_t pk[32], char digest_out[MM_DIGEST_TEXT_LEN + 1]);

/* ---- refusals: the host's reason lines, character for character ---- */

#define MM_REFUSAL_MAX 8
#define MM_REASON_CAP 192            /* "mound_id mismatch: charter is for '<63>', this mound is '<63>'" fits */

/*
 * The reasons a validator produced, as the SAME text Micromound.Protocol produces (values
 * interpolated the same way), so an audit line on a controller reads exactly like one on a Pi
 * and the golden fixture can compare them. Bounded: past MM_REFUSAL_MAX the count keeps rising
 * but the text is dropped.
 */
typedef struct mm_refusal {
    int count;
    char reasons[MM_REFUSAL_MAX][MM_REASON_CAP];
} mm_refusal;

/* The reasons joined by "; " into out (NUL-terminated, truncated to cap). Returns out. */
const char *mm_refusal_join(const mm_refusal *r, char *out, size_t cap);

/* EnvelopeValidator.Validate(envelope, reducedProfile: true). Returns the refusal count (0 = valid). */
int mm_envelope_validate(const mm_envelope_in *e, mm_refusal *out);

/* ---- bodies ---- */

typedef struct mm_charter_in {
    char charter_id[MM_ID_CAP];
    char mound_id[MM_ID_CAP];
    char mission_ref[MM_ID_CAP];
    char issued_at[MM_TIME_TEXT_CAP];
    char expires_at[MM_TIME_TEXT_CAP];
    long long lease_ttl_s;
    char action_ceiling[MM_NAME_CAP];
    char capabilities[MM_MAX_CAPABILITIES][MM_NAME_CAP];
    size_t n_capabilities;
    char routines[MM_MAX_ROUTINES][MM_NAME_CAP];
    size_t n_routines;
    struct { char capability[MM_NAME_CAP]; mm_capability_limits limits; } limits[MM_MAX_LIMITS];
    size_t n_limits;
    char evidence_required_for[MM_MAX_REQUIRED_FOR][MM_NAME_CAP];
    size_t n_evidence_required_for;
    long long evidence_min_interval_s;
    char safe_state[MM_NAME_CAP];
    long long sync_interval_s;
} mm_charter_in;

typedef struct mm_stop_in {
    char reason[MM_DETAIL_CAP];
} mm_stop_in;

typedef struct mm_ack_in {
    char status[MM_NAME_CAP];
    char refers_to[MM_ID_CAP];
    long long through_seq;
    char evidence_ids[MM_MAX_EVIDENCE_IDS][MM_ID_CAP];
    size_t n_evidence_ids;
    char detail[MM_DETAIL_CAP];
} mm_ack_in;

/* One decoded entry of a Dictionary<string, double>. */
typedef struct mm_param_in {
    char key[MM_NAME_CAP];
    double value;
} mm_param_in;

typedef struct mm_action_record_in {
    char action_id[MM_ID_CAP];
    char mission_id[MM_ID_CAP];
    char charter_id[MM_ID_CAP];
    char capability[MM_NAME_CAP];
    char routine_id[MM_NAME_CAP];
    mm_param_in requested_parameters[MM_MAX_PARAMS];
    size_t n_requested_parameters;
    mm_param_in parameters[MM_MAX_PARAMS];
    size_t n_parameters;
    char started_at[MM_TIME_TEXT_CAP];
    char ended_at[MM_TIME_TEXT_CAP];
    char outcome[MM_NAME_CAP];
    int evidence_required;
    char evidence_refs[MM_MAX_EVIDENCE_IDS][MM_ID_CAP];
    size_t n_evidence_refs;
    char detail[MM_DETAIL_CAP];
} mm_action_record_in;

/*
 * Each parser fills its struct with the C# contract's defaults first (so an absent member means the
 * same thing it means to the host), then reads the members present, skipping unknown ones. Returns
 * 0, or -1 with *error = an mm_jr_error (MM_JR_TYPE for a member of the wrong type, MM_JR_TOO_MANY
 * past a capacity, MM_JR_OVERFLOW for a string too long for its field).
 */
int mm_charter_parse(const char *json, size_t n, mm_charter_in *out, int *error);
int mm_stop_parse(const char *json, size_t n, mm_stop_in *out, int *error);
int mm_ack_parse(const char *json, size_t n, mm_ack_in *out, int *error);
int mm_action_record_parse(const char *json, size_t n, mm_action_record_in *out, int *error);

/*
 * CharterValidator.Validate(charter, expectedMoundId, now, deviceCapabilities, deviceRoutines).
 * Pass device_capabilities NULL to skip the presence check (the host does the same with a null set).
 * Returns the refusal count (0 = accept).
 */
int mm_charter_validate(const mm_charter_in *c, const char *expected_mound_id, int64_t now,
                        const char *const *device_capabilities, size_t n_device_capabilities,
                        const char *const *device_routines, size_t n_device_routines,
                        mm_refusal *out);

/* ---- re-encoding a parsed body: bind a view, then hand view.<body> to the mm_bodies writer ---- */

typedef struct mm_charter_view {
    mm_charter charter;
    const char *capabilities[MM_MAX_CAPABILITIES];
    const char *routines[MM_MAX_ROUTINES];
    mm_limit_entry limits[MM_MAX_LIMITS];
    const char *required_for[MM_MAX_REQUIRED_FOR];
} mm_charter_view;
void mm_charter_bind(const mm_charter_in *in, mm_charter_view *view);

typedef struct mm_ack_view {
    mm_ack ack;
    const char *evidence_ids[MM_MAX_EVIDENCE_IDS];
} mm_ack_view;
void mm_ack_bind(const mm_ack_in *in, mm_ack_view *view);

typedef struct mm_action_record_view {
    mm_action_record record;
    mm_param requested_parameters[MM_MAX_PARAMS];
    mm_param parameters[MM_MAX_PARAMS];
    const char *evidence_refs[MM_MAX_EVIDENCE_IDS];
} mm_action_record_view;
void mm_action_record_bind(const mm_action_record_in *in, mm_action_record_view *view);

/* ---- small shared rules ---- */

/* CapabilityPattern.Matches: exact, a trailing ".*" prefix, or "*". */
int mm_capability_pattern_matches(const char *pattern, const char *capability);

/* CapabilityId.IsRoutine: the "routine." namespace. */
int mm_capability_is_routine(const char *id);

/* ActionClasses.TryParse: 0 observe, 1 benign, 2 controlled, 3 hazardous; -1 unknown. */
int mm_action_class_parse(const char *text);

#ifdef __cplusplus
}
#endif

#endif
