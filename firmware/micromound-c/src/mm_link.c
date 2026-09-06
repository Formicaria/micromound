#include "mm_link.h"
#include "mm_enroll.h"
#include "mm_json_read.h"

#include <stdio.h>
#include <string.h>

void mm_link_init(mm_link *link, const mm_hal *hal)
{
    memset(link, 0, sizeof *link);
    link->hal = hal;
}

int mm_link_parse_downlink(const char *body, size_t n, mm_wire *downlink, size_t cap, size_t *n_downlink)
{
    mm_jr r;
    size_t i;
    int more, blank = 1;

    *n_downlink = 0;
    for (i = 0; i < n; i++) if (body[i] != ' ' && body[i] != '\t' && body[i] != '\n' && body[i] != '\r') { blank = 0; break; }
    if (blank) return 0;

    mm_jr_init(&r, body, n);
    if (mm_jr_array_begin(&r) != 0) return -1;
    while ((more = mm_jr_array_next(&r)) == 1) {
        const char *start;
        size_t len;
        if (mm_jr_raw(&r, &start, &len) != 0) return -1;
        if (*n_downlink < cap) {
            downlink[*n_downlink].bytes = start;
            downlink[*n_downlink].n = len;
            (*n_downlink)++;
        }
        /* past cap: still validated and skipped; a controller that sends more than a batch gets the rest next beat */
    }
    if (more != 0 || mm_jr_end(&r) != 0) return -1;
    return 0;
}

int mm_link_exchange(void *ctx, const char *wire, size_t n, mm_wire *downlink, size_t *n_downlink)
{
    mm_link *link = (mm_link *)ctx;
    size_t resp_len = 0;
    int status = 0;

    *n_downlink = 0;
    link->last_status = 0;
    if (link->hal->http_post_json(link->hal->ctx, MM_SYNC_PATH, wire, n, link->response, sizeof link->response, &resp_len, &status) != 0) {
        snprintf(link->last_detail, sizeof link->last_detail, "offline: no exchange");
        return -1;
    }
    link->last_status = status;
    if (status < 200 || status > 299) {
        snprintf(link->last_detail, sizeof link->last_detail, "controller returned HTTP %d", status);
        return -1;   /* not offline exactly, but a failed exchange — the queue retries */
    }
    if (mm_link_parse_downlink(link->response, resp_len, downlink, MM_DEVICE_BATCH, n_downlink) != 0) {
        snprintf(link->last_detail, sizeof link->last_detail, "controller response unreadable");
        *n_downlink = 0;
        return -1;
    }
    snprintf(link->last_detail, sizeof link->last_detail, "exchanged; %d downlink envelope(s)", (int)*n_downlink);
    return 0;
}
