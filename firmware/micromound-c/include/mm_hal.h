/*
 * mm_hal — everything a board must provide, and nothing else.
 *
 * The library above this line (mm_app, mm_enroll, mm_link, mm_drivers) is portable C99 tested on
 * the host against a fake of this interface (tests/test_board.c). A board port fills one struct:
 * firmware/esp32/main/hal_esp32.c does it with ESP-IDF (SNTP clock, esp_random, esp_http_client,
 * NVS, gpio, adc_oneshot). Nothing here allocates; every buffer is the caller's.
 *
 * Every function returns 0 on success and -1 on failure unless stated. A failure is a normal
 * outcome the caller handles (offline, absent key, a line that will not drive) — never a reason
 * to halt.
 */
#ifndef MM_HAL_H
#define MM_HAL_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * ZERO THE STRUCT BEFORE FILLING IT IN. `memset(&hal, 0, sizeof hal)` first, then assign the hooks
 * this board has. Some are OPTIONAL and NULL is how the library is told they are absent, so a board
 * that fills the fields one by one and skips one is passing whatever was on the stack — which is a
 * function pointer the library will call. Every hook added after v0.9.22 is optional for exactly
 * this reason, and this is the price of that: a struct grown by one field will segfault code that
 * assigned the fields one by one and never zeroed the struct. `-Wextra` catches the initializer-list
 * form of the same mistake (missing-field-initializers) but has nothing to say about field-by-field
 * assignment — which is how the test HAL segfaulted the moment `monotonic_s` was added in v0.9.38.
 */
typedef struct mm_hal {
    void *ctx;

    /* Epoch seconds (UTC). Returns 0 while the clock is not yet set — the app will not act on a zero clock. */
    int64_t (*now)(void *ctx);

    /* Cryptographic-quality randomness (seeds, envelope ids). */
    int (*random_bytes)(void *ctx, uint8_t *out, size_t n);

    /*
     * POST body (application/json) to <controller>/<path>. On a completed HTTP exchange returns 0
     * with *status set and the response body copied into resp (NUL-terminated, truncated to cap - 1;
     * *resp_len is the copied length; a body longer than the buffer is truncated and will read as
     * unreadable, which the caller treats as a failed exchange). Returns -1 when no exchange happened
     * at all (no link, DNS, TLS, timeout) — "offline".
     */
    int (*http_post_json)(void *ctx, const char *path, const char *body, size_t body_len,
                          char *resp, size_t cap, size_t *resp_len, int *status);

    /* Protected key/value storage (NVS). get: -1 when absent or larger than cap. set with n == 0 stores an empty value. */
    int (*kv_get)(void *ctx, const char *key, uint8_t *out, size_t cap, size_t *n);
    int (*kv_set)(void *ctx, const char *key, const uint8_t *data, size_t n);

    /* A digital output line. level 1 = high. */
    int (*gpio_write)(void *ctx, int pin, int level);

    /* A digital input line, sampled now: *level 1 = high. A line that cannot be read is a fault, never a 0. */
    int (*gpio_read)(void *ctx, int pin, int *level);

    /* One analog sample, in volts. */
    int (*adc_read)(void *ctx, int channel, double *volts);

    /*
     * OPTIONAL (v0.9.38, roadmap P0.5). Seconds since some fixed point this board cannot change —
     * an uptime counter, not a clock. May be NULL, and NULL is a supported configuration, not a
     * lapse: the library then measures the operating budgets on `now` alone, exactly as it did
     * before this existed.
     *
     * What it buys: `now` is a WALL clock and a wall clock can be STEPPED, and a step forward is
     * indistinguishable from time passing. A board whose RTC is slow, corrected by its first SNTP
     * sync, would otherwise find every min_off_s elapsed and every max_rate_per_h window empty at
     * the exact moment it has least reason to trust its own sense of time. With this, the kernel
     * ages those budgets on whichever of the two clocks claims LESS time passed. On ESP-IDF this
     * is esp_timer_get_time() / 1000000.
     */
    int64_t (*monotonic_s)(void *ctx);
} mm_hal;

/* The kv keys the library uses (at most 15 characters: NVS's key limit). A board stores nothing else on the library's behalf. */
#define MM_KV_SEED "mm.seed"                    /* 32 bytes: the device's Ed25519 seed; never leaves the device */
#define MM_KV_CONTROLLER_PK "mm.ctl_pk"         /* 32 bytes: received at enrollment */
#define MM_KV_SYNC_INTERVAL "mm.sync_s"         /* text: the controller's cadence from enrollment */
#define MM_KV_ENROLL_TOKEN "mm.token"           /* text: the one-time token, provisioned; burned on success or definite refusal */
#define MM_KV_STOPPED "mm.stopped"              /* 1 byte: a sticky stop. Present and '1' = stopped, and a restart NEVER clears it */

#ifdef __cplusplus
}
#endif

#endif
