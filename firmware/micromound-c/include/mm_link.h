/*
 * mm_link — HttpSyncTransport over the HAL: ISyncTransport.TryExchange for a board.
 *
 * POST one signed envelope to <controller>/micromound/v0/sync; the response is a JSON array of
 * envelopes (or empty: "nothing downlink", not an error). A non-2xx status is a failed exchange
 * (the queue retries); no HTTP exchange at all is offline. Either way the device's next beat
 * resumes from exactly where this one stopped. The response is kept in the link's own buffer, so
 * the downlink slices handed to mm_device stay valid until the next exchange — the contract
 * mm_exchange_fn promises.
 */
#ifndef MM_LINK_H
#define MM_LINK_H

#include <stddef.h>
#include "mm_device.h"
#include "mm_hal.h"

#ifdef __cplusplus
extern "C" {
#endif

#ifndef MM_LINK_RESPONSE_CAP
#define MM_LINK_RESPONSE_CAP 8192      /* one exchange's downlink; a batch of MM_DEVICE_BATCH small envelopes fits */
#endif

typedef struct mm_link {
    const mm_hal *hal;
    char response[MM_LINK_RESPONSE_CAP];
    int last_status;                   /* the last HTTP status, 0 when the last exchange never happened */
    char last_detail[MM_REASON_CAP];   /* HttpSyncTransport's detail line (without the host's exception-message suffix) */
} mm_link;

void mm_link_init(mm_link *link, const mm_hal *hal);

/* The mm_exchange_fn to hand mm_device_sync; ctx is the mm_link. */
int mm_link_exchange(void *ctx, const char *wire, size_t n, mm_wire *downlink, size_t *n_downlink);

/*
 * ParseDownlink on its own: splits a JSON array of objects into raw slices (validated as JSON as
 * they are skipped). Whitespace-only is zero items. Returns 0, or -1 on a body that is not a JSON
 * array of values.
 */
int mm_link_parse_downlink(const char *body, size_t n, mm_wire *downlink, size_t cap, size_t *n_downlink);

#ifdef __cplusplus
}
#endif

#endif
