#include "mm_app.h"
#include "mm_ed25519.h"
#include "mm_sha256.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void set_str(char *dst, size_t cap, const char *src)
{
    size_t n = strlen(src);
    if (n >= cap) n = cap - 1;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

/* ---- identity ------------------------------------------------------------------------------ */

/* A version-4 UUID string from the board's RNG (RFC 4122 layout: version and variant bits set). */
static void new_id(void *ctx, char out[MM_ID_CAP])
{
    mm_app *app = (mm_app *)ctx;
    uint8_t b[16];
    if (app->hal->random_bytes(app->hal->ctx, b, sizeof b) != 0) {
        /* an RNG that fails is a board fault; fall back to something unique-enough so the envelope still has an id */
        static unsigned counter = 0;
        memset(b, 0, sizeof b);
        counter++;
        b[12] = (uint8_t)(counter >> 24); b[13] = (uint8_t)(counter >> 16); b[14] = (uint8_t)(counter >> 8); b[15] = (uint8_t)counter;
    }
    b[6] = (uint8_t)((b[6] & 0x0F) | 0x40);
    b[8] = (uint8_t)((b[8] & 0x3F) | 0x80);
    snprintf(out, MM_ID_CAP, "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
             b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);
}

/* The stored seed blob: the 32 secret bytes and the first 4 of their SHA-256. */
#define MM_SEED_BLOB 36

static void seed_checksum(const uint8_t seed[32], uint8_t out[4])
{
    uint8_t digest[MM_SHA256_DIGEST_LEN];
    mm_sha256_digest(seed, 32, digest);
    memcpy(out, digest, 4);
}

static int store_seed(mm_app *app)
{
    uint8_t blob[MM_SEED_BLOB];
    memcpy(blob, app->seed, 32);
    seed_checksum(app->seed, blob + 32);
    return app->hal->kv_set(app->hal->ctx, MM_KV_SEED, blob, sizeof blob);
}

/*
 * The device's identity, and the three ways this can go (v0.9.41, roadmap P0.9).
 *
 * The rule that governs all of it: an identity that EXISTS must never be silently replaced. Before
 * this, any read that did not produce exactly 32 bytes fell straight through to minting a new seed
 * and overwriting the old one — so one truncated read, one storage hiccup, one partially-written
 * blob, and the mound came up as a different device. It would look healthy: it signs, it beats, it
 * enrolls; and the controller rejects every envelope it sends, because they are signed by a mound
 * nobody knows, while the real mound's whole signed history is orphaned. Reprovisioning is a
 * decision, and it is not this function's to make.
 *
 *   absent            -> a fresh device: mint, store, run.
 *   present and good  -> use it. A 32-byte blob is the pre-v0.9.41 form and is rewritten with its
 *                        checksum in place; a failed rewrite is not fatal, the next boot retries.
 *   present and bad   -> halt. Either the bytes are the wrong length, or the checksum says they are
 *                        not the bytes that were written, or the store faulted on a key it may well
 *                        still hold. The board stops with its outputs safe and says which.
 *
 * The checksum is this library's own, deliberately, and not a reliance on NVS's per-entry CRC:
 * mm_hal is an abstraction, a board may bind kv to anything, and the one datum whose corruption
 * cannot be noticed any other way is the one that must not be guessed at.
 */
static int load_or_create_seed(mm_app *app, char *error, size_t cap)
{
    uint8_t blob[MM_SEED_BLOB], expect[4];
    size_t n = 0;
    int rc = app->hal->kv_get(app->hal->ctx, MM_KV_SEED, blob, sizeof blob, &n);

    if (rc == 0) {
        if (n != MM_SEED_BLOB && n != 32) {
            snprintf(error, cap, "the stored device seed is %u bytes, not %d or 32; refusing to replace an identity that exists",
                     (unsigned)n, MM_SEED_BLOB);
            return -1;
        }
        memcpy(app->seed, blob, 32);
        if (n == MM_SEED_BLOB) {
            seed_checksum(app->seed, expect);
            if (memcmp(expect, blob + 32, 4) != 0) {
                set_str(error, cap, "the stored device seed failed its checksum; refusing to run as a mound "
                                    "whose identity may not be its own");
                return -1;
            }
            return 0;
        }
        (void)store_seed(app);      /* legacy 32-byte seed: add the checksum, retry next boot if it fails */
        return 0;
    }

    if (rc != MM_KV_ABSENT) {
        set_str(error, cap, "protected storage faulted reading the device seed; refusing to mint a new identity "
                            "over one that may still be there");
        return -1;
    }

    if (app->hal->random_bytes(app->hal->ctx, app->seed, sizeof app->seed) != 0) {
        set_str(error, cap, "no entropy for the device seed; refusing to run with a weak identity");
        return -1;
    }
    if (store_seed(app) != 0) {
        set_str(error, cap, "the device seed could not be stored; an identity that does not survive a reboot is not one");
        return -1;
    }
    return 0;
}

/* ---- safety -------------------------------------------------------------------------------- */

static void safe_state_cb(void *ctx)
{
    mm_app_enter_safe_state((mm_app *)ctx);
}

int mm_app_enter_safe_state(mm_app *app)
{
    size_t i;
    int failed = 0;
    for (i = 0; i < app->cfg.n_relays; i++)
        if (mm_relay_safe(&app->cfg.relays[i]) != 0) failed = 1;
    if (failed && !app->status.tripped) {
        app->status.tripped = 1;
        mm_authority_stop(&app->device.kernel.authority);   /* a line that will not de-energize: treat as unsafe, stop everything */
    }
    return failed ? -1 : 0;
}

/* ---- init ---------------------------------------------------------------------------------- */

int mm_app_init(mm_app *app, const mm_hal *hal, const mm_app_config *cfg, char *error, size_t error_cap)
{
    mm_device_config dcfg;
    size_t i, n = 0;
    uint8_t interval[32];

    memset(app, 0, sizeof *app);
    app->hal = hal;
    app->cfg = *cfg;
    if (error_cap) error[0] = '\0';

    if (load_or_create_seed(app, error, error_cap) != 0) return -1;
    mm_ed25519_seed_keypair(app->pk, app->sk, app->seed);

    if (hal->kv_get(hal->ctx, MM_KV_CONTROLLER_PK, app->controller_pk, sizeof app->controller_pk, &n) == 0 && n == 32)
        app->status.enrolled = 1;

    app->sync_interval_s = MM_APP_DEFAULT_SYNC_S;
    if (hal->kv_get(hal->ctx, MM_KV_SYNC_INTERVAL, interval, sizeof interval - 1, &n) == 0 && n > 0) {
        interval[n] = '\0';
        { double v = strtod((const char *)interval, NULL); if (v > 0 && v < 86400) app->sync_interval_s = (int)(v + 0.5); }
    }

    memset(&dcfg, 0, sizeof dcfg);
    dcfg.mound_id = cfg->mound_id;
    dcfg.secret_key = app->sk;
    dcfg.controller_public_key = app->controller_pk;   /* zeros until enrolled; nothing verifies against zeros */
    dcfg.caps = cfg->caps; dcfg.n_caps = cfg->n_caps;
    dcfg.routines = cfg->routines; dcfg.n_routines = cfg->n_routines;
    dcfg.new_id = new_id; dcfg.new_id_ctx = app;
    dcfg.enter_safe_state = safe_state_cb; dcfg.safe_state_ctx = app;
    if (mm_device_init(&app->device, &dcfg, error, error_cap) != 0) return -1;
    mm_kernel_apply_device_limits(&app->device.kernel, cfg->device_limits, cfg->n_device_limits, NULL);

    for (i = 0; i < cfg->n_relays; i++)
        if (mm_kernel_bind_executor(&app->device.kernel, &cfg->relays[i].executor) != 0) {
            snprintf(error, error_cap, "relay '%s' names no compiled capability", cfg->relays[i].capability);
            return -1;
        }
    for (i = 0; i < cfg->n_probes; i++)
        if (mm_kernel_bind_executor(&app->device.kernel, &cfg->probes[i].executor) != 0) {
            snprintf(error, error_cap, "probe '%s' names no compiled capability", cfg->probes[i].capability);
            return -1;
        }
    for (i = 0; i < cfg->n_switches; i++)
        if (mm_kernel_bind_executor(&app->device.kernel, &cfg->switches[i].executor) != 0) {
            snprintf(error, error_cap, "switch '%s' names no compiled capability", cfg->switches[i].capability);
            return -1;
        }
    if (cfg->n_schedule > MM_APP_MAX_SCHEDULE) { set_str(error, error_cap, "too many schedule entries"); return -1; }

    mm_link_init(&app->link, hal);

    /*
     * A STOP SURVIVES THE REBOOT (v0.9.35, roadmap P0.6).
     *
     * SAFETY.md has always said a restart never clears a stop, and on this device it did: the stop
     * lived in RAM only, so power-cycling a stopped mound brought it back willing to actuate. That
     * is the one thing a reboot must never be able to do, and it was the cheapest thing here to fix
     * — one byte.
     *
     * Applied BEFORE anything can act: the authority is stopped and the hardware driven safe, in
     * that order, so a board that comes up hot goes cold without waiting for a tick.
     */
    {
        uint8_t stopped = 0;
        size_t got = 0;
        /* PRESENCE is the signal, not the byte (v0.9.41). Nothing can accidentally create a key, but
           a flipped bit can change one — and of the two directions a corrupted stop could go, only
           "still stopped" is safe. The byte is written as '1' for an operator reading the flash. */
        if (hal->kv_get(hal->ctx, MM_KV_STOPPED, &stopped, sizeof stopped, &got) == 0 && got >= 1) {
            mm_authority_stop(&app->device.kernel.authority);
            app->status.stopped_at_boot = 1;
            safe_state_cb(app);
        }
    }

    return 0;
}

/* Write the sticky stop through, once, the moment the mound becomes stopped. */
static void persist_stop(mm_app *app)
{
    static const uint8_t one = '1';
    if (app->stop_persisted) return;
    if (app->hal->kv_set(app->hal->ctx, MM_KV_STOPPED, &one, 1) == 0) app->stop_persisted = 1;
}

/* ---- enrollment ---------------------------------------------------------------------------- */

static void try_enroll(mm_app *app, int64_t now)
{
    uint8_t token[MM_APP_TOKEN_CAP];
    size_t n = 0, i;
    const char *caps[MM_MAX_CAPABILITY_DESCS];
    mm_enroll_request req;
    mm_enrollment result;
    int rc;

    app->status.next_enroll_attempt_at = now + MM_APP_ENROLL_RETRY_S;
    if (app->hal->kv_get(app->hal->ctx, MM_KV_ENROLL_TOKEN, token, sizeof token - 1, &n) != 0 || n == 0) {
        set_str(app->status.last_detail, sizeof app->status.last_detail, "not enrolled and no enrollment token is provisioned");
        return;
    }
    token[n] = '\0';

    for (i = 0; i < app->cfg.n_caps; i++) caps[i] = app->cfg.caps[i].id;
    memset(&req, 0, sizeof req);
    req.token = (const char *)token;
    req.mound_id = app->cfg.mound_id;
    req.device_public_key = app->pk;
    req.hardware_profile = app->cfg.hardware_profile;
    req.tier = MM_TIER_DETERMINISTIC_CONTROLLER;
    req.capabilities = caps;
    req.n_capabilities = app->cfg.n_caps;

    app->status.enroll_attempts++;
    rc = mm_enroll(app->hal, &req, &result, app->status.last_detail, sizeof app->status.last_detail);
    if (rc == 1) {
        memcpy(app->controller_pk, result.controller_public_key, 32);
        app->status.enrolled = 1;
        if (result.has_sync_interval) app->sync_interval_s = (int)(result.sync_interval_s + 0.5);
        app->hal->kv_set(app->hal->ctx, MM_KV_ENROLL_TOKEN, (const uint8_t *)"", 0);   /* burned */
    } else if (rc == 0 && strncmp(app->status.last_detail, "enrollment refused", 18) == 0) {
        app->hal->kv_set(app->hal->ctx, MM_KV_ENROLL_TOKEN, (const uint8_t *)"", 0);   /* a definite refusal: the token is spent */
    }
}

/* ---- the tick ------------------------------------------------------------------------------ */

void mm_app_tick(mm_app *app, int64_t now)
{
    size_t i;
    int interval;

    if (now == 0) now = app->hal->now(app->hal->ctx);
    if (now == 0) return;                                        /* no clock: nothing is signed, nothing actuates */

    /*
     * The clock nobody can step, refreshed once per tick (v0.9.38, roadmap P0.5). `now` above is a
     * WALL clock: a board whose RTC is slow until its first sync sees that correction as time
     * passing, and every min_off_s and max_rate_per_h window would empty at once. The kernel ages
     * those budgets on whichever of the two claims LESS. A HAL that offers no monotonic source
     * leaves this 0, which is wall-clock only — the behaviour before this existed.
     */
    app->device.kernel.history.monotonic_now =
        app->hal->monotonic_s ? app->hal->monotonic_s(app->hal->ctx) : 0;

    /* A stop is durable the moment it exists. This is the catch-all — anything that stopped the
       mound since the last tick is written through before this one does any work — and the two
       calls below close the window on the two things that can stop it DURING a tick: a relay that
       will not release, and a downlinked stop. This function is the only place with storage. */
    if (app->device.kernel.authority.stopped) persist_stop(app);

    /* holds first: a relay whose time is up is released before anything else happens this tick */
    for (i = 0; i < app->cfg.n_relays; i++)
        if (mm_relay_service(&app->cfg.relays[i], now) != 0 && !app->status.tripped) {
            app->status.tripped = 1;
            mm_authority_stop(&app->device.kernel.authority);
            persist_stop(app);                                   /* before the beat, before the retry */
        }

    if (!app->status.enrolled) {
        if (now >= app->status.next_enroll_attempt_at) try_enroll(app, now);
        return;                                                  /* nothing is verifiable without the controller key */
    }

    mm_device_tick(&app->device, now);

    /* the beat, on the charter's cadence when chartered, enrollment's otherwise */
    interval = app->device.kernel.authority.has_charter && app->device.kernel.authority.charter.sync_interval_s > 0
        ? (int)app->device.kernel.authority.charter.sync_interval_s : app->sync_interval_s;
    if (app->status.beats == 0 || now - app->status.last_beat_at >= interval) {
        mm_sync_outcome out;
        mm_device_sync(&app->device, now, mm_link_exchange, &app->link, &out);
        app->status.last_beat_at = now;
        app->status.beats++;
        set_str(app->status.last_detail, sizeof app->status.last_detail, app->link.last_detail);
        if (app->device.kernel.authority.stopped) persist_stop(app);   /* a stop arrived on this beat */
    }

    /* compiled routines: each on its period, through the kernel (which refuses when it must) */
    for (i = 0; i < app->cfg.n_schedule; i++) {
        const mm_schedule_entry *s = &app->cfg.schedule[i];
        if (app->last_run[i] != 0 && now - app->last_run[i] < s->period_s) continue;
        {
            mm_request req;
            mm_action_record_in record;
            memset(&req, 0, sizeof req);
            req.capability = s->capability;
            req.parameters = s->parameters;
            req.n_parameters = s->n_parameters;
            req.mission_id = "";
            req.worker = "";
            req.worker_ceiling = -1;
            mm_device_act(&app->device, &req, now, &record);
            app->status.actions++;
        }
        app->last_run[i] = now;
    }
}
