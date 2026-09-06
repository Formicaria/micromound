/*
 * mm_ports — the board as a port server (PROTOCOL.md §12, "port requests"): the subordinate
 * arrangement of the acceptance bench, where a Pi-class mound's OWN kernel authorizes every action
 * and sends the board a bounded request — write this pin, read this channel — over the same framing
 * the bridge uses, with the roles reversed (the Pi asks, the board answers).
 *
 * The board holds no key, signs nothing and decides nothing about authority. What it does hold is
 * the innermost limit tier — the hardware's own — and two things a dumb expander would not:
 *
 *   max_on_s   a pin driven active is released by the BOARD when its compiled max_on_s passes,
 *              whatever the Pi says or fails to say (the firmware tier of "hardware ∩ device ∩
 *              charter"; a charter can only narrow it, and the Pi's kernel already did);
 *   watchdog   a link that goes quiet for watchdog_s drives every pin safe — disconnection never
 *              creates authority, and a Pi that stopped talking cannot be assumed to still mean
 *              what it last said.
 *
 * Paths (request payloads are JSON objects; responses are JSON objects with an HTTP-style status):
 *   micromound/link/ports/hello  {}                         -> 200 {"profile","firmware","watchdog_s","tripped","pins":[{"pin","active_high","max_on_s","level"}],"channels":[n,…]}
 *   micromound/link/ports/write  {"pin":5,"level":true}     -> 200 {"pin":5,"level":true} | 404 unknown pin | 409 tripped | 503 the line would not drive
 *   micromound/link/ports/read   {"channel":0}              -> 200 {"channel":0,"volts":0.75}   | 404 unknown channel | 503 sensor read failed
 * Anything else -> 404. A malformed body -> 400. Every request feeds the watchdog.
 *
 * Fixture: tests/Micromound.Tests/Golden/files/port-exchange.txt is WRITTEN by test_ports.c (every
 * request the host's LinkPorts client sends and the board's exact answer) and READ by the host's
 * LinkPortsTests, so the Pi parses precisely what the board produces.
 */
#ifndef MM_PORTS_H
#define MM_PORTS_H

#include <stddef.h>
#include <stdint.h>
#include "mm_frame.h"
#include "mm_hal.h"

#ifdef __cplusplus
extern "C" {
#endif

#define MM_PORTS_PATH_HELLO "micromound/link/ports/hello"
#define MM_PORTS_PATH_WRITE "micromound/link/ports/write"
#define MM_PORTS_PATH_READ "micromound/link/ports/read"
#define MM_PORTS_MAX_PINS 16
#define MM_PORTS_MAX_CHANNELS 8
#define MM_PORTS_RESPONSE_CAP 1024

typedef struct mm_port_pin {
    int pin;
    int active_high;
    double max_on_s;                       /* 0 = no hardware bound (not recommended) */
    int active;                            /* driven active now */
    int64_t held_until;                    /* when max_on_s releases it; 0 = no bound running */
    int release_failed;
    unsigned writes, auto_releases;
} mm_port_pin;

typedef struct mm_ports {
    const mm_hal *hal;
    const char *profile;
    const char *firmware;
    mm_port_pin pins[MM_PORTS_MAX_PINS];
    size_t n_pins;
    int channels[MM_PORTS_MAX_CHANNELS];
    size_t n_channels;
    int64_t watchdog_s;                    /* 0 = no watchdog (not recommended) */
    int64_t last_request_at;               /* 0 = nothing yet: the watchdog is not armed until the first request */
    int watchdog_fired;                    /* the last quiet period drove the pins safe; cleared by the next request */
    int tripped;                           /* a release write failed: no pin is driven active again until reboot */
    unsigned requests, watchdog_trips, rejected;
} mm_ports;

void mm_ports_init(mm_ports *p, const mm_hal *hal, const char *profile, const char *firmware, int64_t watchdog_s);

/* Adds a pin (driven to its safe level now) or a channel. Returns 0, or -1 (table full, duplicate, or the safe write failed). */
int mm_ports_add_pin(mm_ports *p, int pin, int active_high, double max_on_s);
int mm_ports_add_channel(mm_ports *p, int channel);

/*
 * Handles one request. Writes the response body into resp (NUL-terminated, *resp_len its length) and
 * returns the status. `now` is any monotonic clock in seconds — a wall clock is not needed.
 */
int mm_ports_handle(mm_ports *p, const char *path, size_t path_len, const char *body, size_t body_len,
                    int64_t now, char *resp, size_t cap, size_t *resp_len);

/* The tick: release pins whose max_on_s passed, fire the watchdog. Returns -1 once tripped. */
int mm_ports_service(mm_ports *p, int64_t now);

/* Every pin to its safe level. Returns -1 when a write failed (and marks the trip). */
int mm_ports_safe(mm_ports *p);

/*
 * One decoded request frame -> one response frame in out. Returns the frame length, or 0 when the
 * frame was not a request or the response does not fit. The board's serve loop is: feed bytes into a
 * decoder; when it completes, call this and write out.
 */
size_t mm_ports_on_frame(mm_ports *p, const mm_frame_decoder *d, int64_t now, uint8_t *out, size_t cap);

#ifdef __cplusplus
}
#endif

#endif
