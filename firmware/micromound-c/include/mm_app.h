/*
 * mm_app — the whole firmware above the HAL: what app_main drives, one tick at a time.
 *
 *   identity    the Ed25519 seed lives in protected storage (MM_KV_SEED); created from the board's
 *               RNG on first boot, never regenerated, never read back out. The controller key
 *               (MM_KV_CONTROLLER_PK) is what enrollment delivered.
 *   enrollment  until a controller key is stored, every tick that is due tries the one-time token
 *               in MM_KV_ENROLL_TOKEN (PROTOCOL.md §3). A definite refusal burns the token; an
 *               outage retries; success stores the key and the controller's sync cadence.
 *   the loop    once enrolled: quiesce when the lease runs out, release elapsed relay holds, beat
 *               on the sync cadence (the charter's sync_interval_s when chartered, enrollment's
 *               otherwise, MM_APP_DEFAULT_SYNC_S failing both), and run the compiled routines
 *               (a routine table entry is a capability request on a period — through the kernel,
 *               recorded, queued).
 *   safety      stop and quiesce release every relay (mm_device's safe-state callback); a relay whose
 *               release write fails is reported as a trip (mm_app_status.tripped) and the app stops
 *               the mound, because a line that will not de-energize must be treated as unsafe
 *               (SAFETY.md). The app never actuates on a zero clock.
 *
 * Nothing here allocates; the tables and the drivers are the board's `static` objects.
 */
#ifndef MM_APP_H
#define MM_APP_H

#include <stdint.h>
#include "mm_device.h"
#include "mm_drivers.h"
#include "mm_enroll.h"
#include "mm_hal.h"
#include "mm_link.h"

#ifdef __cplusplus
extern "C" {
#endif

#define MM_APP_MAX_RELAYS 8
#define MM_APP_MAX_PROBES 8
#define MM_APP_MAX_SWITCHES 8
#define MM_APP_MAX_SCHEDULE 8
#define MM_APP_DEFAULT_SYNC_S 15
#define MM_APP_ENROLL_RETRY_S 30
#define MM_APP_TOKEN_CAP 256                          /* a longer provisioned token reads as absent */

/* One compiled routine: request `capability` with `parameters` every `period_s`. */
typedef struct mm_schedule_entry {
    const char *capability;
    const mm_param *parameters;
    size_t n_parameters;
    int64_t period_s;
} mm_schedule_entry;

typedef struct mm_app_config {
    const char *mound_id;
    const char *hardware_profile;                 /* what enrollment reports; the host joins its capabilities with ',' */
    const mm_capability_desc *caps;   size_t n_caps;
    const mm_routine_desc *routines;  size_t n_routines;
    const mm_device_limit *device_limits; size_t n_device_limits;
    mm_relay *relays;   size_t n_relays;          /* initialised by the board (mm_relay_init) */
    mm_probe *probes;   size_t n_probes;          /* initialised by the board (mm_probe_init) */
    mm_switch *switches; size_t n_switches;       /* initialised by the board (mm_switch_init) */
    const mm_schedule_entry *schedule; size_t n_schedule;
} mm_app_config;

typedef struct mm_app_status {
    int enrolled;
    int tripped;                                  /* a relay would not release; the mound was stopped (the beat still goes out, so the controller hears it) */
    int64_t last_beat_at;
    int64_t next_enroll_attempt_at;
    int beats, actions, enroll_attempts;
    char last_detail[MM_REASON_CAP];              /* the last enrollment or sync detail line; a trip is `tripped`, not a line here */
} mm_app_status;

typedef struct mm_app {
    const mm_hal *hal;
    mm_app_config cfg;
    uint8_t seed[32], sk[64], pk[32], controller_pk[32];
    mm_device device;                             /* large */
    mm_link link;                                 /* large */
    mm_app_status status;
    int64_t last_run[MM_APP_MAX_SCHEDULE];
    int sync_interval_s;
} mm_app;

/*
 * Loads or creates the identity, loads the controller key if enrolled, initialises the device and
 * binds every relay and probe as an executor. Returns 0, or -1 with the reason in error.
 */
int mm_app_init(mm_app *app, const mm_hal *hal, const mm_app_config *cfg, char *error, size_t error_cap);

/* One pass of the service loop at `now` (the HAL clock is read when now is 0). */
void mm_app_tick(mm_app *app, int64_t now);

/* Release every relay; on a release failure, stop the mound and mark the trip. Returns -1 on a trip. */
int mm_app_enter_safe_state(mm_app *app);

#ifdef __cplusplus
}
#endif

#endif
