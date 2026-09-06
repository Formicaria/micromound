/*
 * mm_enroll — PROTOCOL.md §3, the one exchange that is HTTP JSON and not an envelope.
 *
 * Mirrors Micromound.Host.HttpEnrollmentClient: the same request body (same members, same order,
 * same casing), the same reading of the response, the same verdicts in the same words — so a
 * controller sees a constrained device enroll exactly as a Pi does, and an operator reads the same
 * detail line either way. Fixture: tests/Micromound.Tests/Golden/files/enroll-exchange.txt. The two
 * lines the host suffixes with an exception message ("controller unreachable; not enrolled yet",
 * "enrollment response unreadable") are the prefix alone here.
 *
 * The token is one-time: a burned token is a definite refusal, not something to retry. The
 * controller key it returns is the only key downlink is ever verified against, and it is persisted
 * before anything else happens.
 */
#ifndef MM_ENROLL_H
#define MM_ENROLL_H

#include <stddef.h>
#include <stdint.h>
#include "mm_decode.h"
#include "mm_hal.h"

#ifdef __cplusplus
extern "C" {
#endif

#define MM_ENROLL_PATH "micromound/v0/enroll"
#define MM_SYNC_PATH "micromound/v0/sync"
#define MM_TIER_DETERMINISTIC_CONTROLLER "deterministic_controller"
#define MM_TIER_EDGE_QUEEN "edge_queen"

typedef struct mm_enroll_request {
    const char *token;
    const char *mound_id;
    const uint8_t *device_public_key;       /* 32 bytes */
    const char *hardware_profile;           /* the host sends its capabilities joined by ',' */
    const char *tier;                       /* MM_TIER_DETERMINISTIC_CONTROLLER for a device */
    const char *const *capabilities;
    size_t n_capabilities;
    /* driver_schemas is always [] from a device: its hardware is compiled in, not described */
} mm_enroll_request;

typedef struct mm_enrollment {
    uint8_t controller_public_key[32];
    char controller_mound_id[MM_ID_CAP];    /* "" when the controller did not say */
    int has_sync_interval;
    double sync_interval_s;
    int has_protocol_version;
    long long protocol_version;
    char colony_version[MM_NAME_CAP];
} mm_enrollment;

/* The request body as HttpEnrollmentClient serializes it. Returns the length, or 0 if it does not fit. */
size_t mm_enroll_request_body(const mm_enroll_request *r, char *out, size_t cap);

/*
 * HttpEnrollmentClient.TryEnroll's reading of one HTTP exchange. status is the HTTP status; body the
 * response body. Returns 1 (enrolled; *out filled) or 0 (not enrolled), and writes the host's
 * detail line into detail either way.
 */
int mm_enroll_read_response(const mm_enroll_request *r, int status, const char *body, size_t n,
                            mm_enrollment *out, char *detail, size_t detail_cap);

/*
 * The whole exchange over the HAL: POST, read, and — on success — persist the controller key and
 * the sync interval. Returns 1 enrolled, 0 refused or deferred (detail says which), -1 offline.
 */
int mm_enroll(const mm_hal *hal, const mm_enroll_request *r, mm_enrollment *out, char *detail, size_t detail_cap);

#ifdef __cplusplus
}
#endif

#endif
