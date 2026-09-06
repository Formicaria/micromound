/*
 * mm_serial — the HAL's http_post_json over a byte stream, framed by mm_frame (PROTOCOL.md §12).
 *
 * A board wired to a Pi by UART or USB-serial has no Wi-Fi, no TLS and no clock of its own. It
 * makes exactly the exchanges it would make over HTTPS — enrollment, the sync beat — as request
 * frames, and a bridge on the Pi performs the HTTPS half and frames the answer back. Nothing the
 * board signs is touched in between; a bridge that altered a byte would produce an envelope the
 * controller refuses. The bridge also answers one request of its own, MM_LINK_TIME_PATH, with the
 * Pi's clock, so the board can have one (mm_serial_time).
 *
 * The link is synchronous: one request in flight, the response awaited with a first-byte timeout
 * (the bridge's own HTTPS round trip) and an inter-byte timeout (the stream stalled). A timeout is
 * offline, as the HAL defines it; a response for another seq is a late answer to a request the
 * board already gave up on, and is ignored.
 */
#ifndef MM_SERIAL_H
#define MM_SERIAL_H

#include <stddef.h>
#include <stdint.h>
#include "mm_frame.h"

#ifdef __cplusplus
extern "C" {
#endif

#define MM_LINK_TIME_PATH "micromound/link/time"
#ifndef MM_SERIAL_REQUEST_CAP
#define MM_SERIAL_REQUEST_CAP 3072         /* an enrollment body (2 KB) plus its path, framed */
#endif

typedef struct mm_serial_io {
    void *ctx;
    /* Writes all n bytes. Returns 0, or -1. */
    int (*write)(void *ctx, const uint8_t *bytes, size_t n);
    /* Reads one byte, waiting up to timeout_ms. Returns 1 (byte read), 0 (timeout), -1 (the stream is gone). */
    int (*read_byte)(void *ctx, uint8_t *out, int timeout_ms);
} mm_serial_io;

typedef struct mm_serial_link {
    mm_serial_io io;
    uint8_t seq;
    int first_byte_timeout_ms;             /* the bridge's HTTPS round trip; default 15000 */
    int inter_byte_timeout_ms;             /* default 1000 */
    mm_frame_decoder decoder;              /* large: MM_FRAME_MAX_PAYLOAD */
    uint8_t out[MM_SERIAL_REQUEST_CAP];
    unsigned requests, timeouts, stale_responses;
} mm_serial_link;

void mm_serial_init(mm_serial_link *link, const mm_serial_io *io);

/*
 * mm_hal.http_post_json over the link (ctx is the mm_serial_link). Returns 0 with *status and the body
 * (a bridge status of 0 — it could not exchange — is reported as -1, offline, like a Wi-Fi board's), or
 * -1 when nothing came back in time.
 */
int mm_serial_post_json(void *ctx, const char *path, const char *body, size_t body_len,
                        char *resp, size_t cap, size_t *resp_len, int *status);

/* Asks the bridge for its clock. Returns UTC epoch seconds, or 0 when the bridge did not answer. */
int64_t mm_serial_time(mm_serial_link *link);

#ifdef __cplusplus
}
#endif

#endif
