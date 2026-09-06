#include "mm_serial.h"
#include "mm_json_read.h"

#include <string.h>

void mm_serial_init(mm_serial_link *link, const mm_serial_io *io)
{
    memset(link, 0, sizeof *link);
    link->io = *io;
    link->first_byte_timeout_ms = 15000;
    link->inter_byte_timeout_ms = 1000;
    mm_frame_decoder_init(&link->decoder);
}

/* Waits for the response frame to request `seq`. Returns 0 with the decoder holding it, or -1. */
static int await_response(mm_serial_link *link, uint8_t seq)
{
    int timeout = link->first_byte_timeout_ms;
    for (;;) {
        uint8_t byte;
        int rc = link->io.read_byte(link->io.ctx, &byte, timeout);
        if (rc < 0) return -1;
        if (rc == 0) { link->timeouts++; return -1; }
        timeout = link->inter_byte_timeout_ms;
        if (!mm_frame_feed(&link->decoder, byte)) continue;
        if (link->decoder.type != MM_FRAME_RESPONSE) continue;          /* a request from the far side: not ours to answer */
        if (link->decoder.seq != seq) { link->stale_responses++; continue; }
        return 0;
    }
}

int mm_serial_post_json(void *ctx, const char *path, const char *body, size_t body_len,
                        char *resp, size_t cap, size_t *resp_len, int *status)
{
    mm_serial_link *link = (mm_serial_link *)ctx;
    uint8_t payload[MM_SERIAL_REQUEST_CAP];
    size_t n, frame_len;
    uint8_t seq;
    const uint8_t *rbody;
    size_t rlen;

    *resp_len = 0;
    if (cap) resp[0] = '\0';
    *status = 0;

    n = mm_frame_request_payload(path, body, body_len, payload, sizeof payload);
    if (n == 0) return -1;
    seq = ++link->seq;
    frame_len = mm_frame_encode(MM_FRAME_REQUEST, seq, payload, n, link->out, sizeof link->out);
    if (frame_len == 0) return -1;
    link->requests++;
    if (link->io.write(link->io.ctx, link->out, frame_len) != 0) return -1;

    if (await_response(link, seq) != 0) return -1;
    if (mm_frame_parse_response(link->decoder.payload, link->decoder.payload_len, status, &rbody, &rlen) != 0) return -1;
    if (*status == 0) return -1;                                          /* the bridge could not exchange: offline */
    if (cap == 0) return 0;
    if (rlen > cap - 1) rlen = cap - 1;                                   /* truncated: reads as unreadable upstream, a failed exchange */
    memcpy(resp, rbody, rlen);
    resp[rlen] = '\0';
    *resp_len = rlen;
    return 0;
}

int64_t mm_serial_time(mm_serial_link *link)
{
    char resp[64];
    size_t n = 0;
    int status = 0;
    mm_jr r;
    char key[16];
    int more;
    long long epoch = 0;

    if (mm_serial_post_json(link, MM_LINK_TIME_PATH, "{}", 2, resp, sizeof resp, &n, &status) != 0) return 0;
    if (status < 200 || status > 299) return 0;
    mm_jr_init(&r, resp, n);
    if (mm_jr_object_begin(&r) != 0) return 0;
    while ((more = mm_jr_object_next(&r, key, sizeof key)) == 1) {
        if (strcmp(key, "epoch_s") == 0) { if (mm_jr_int(&r, &epoch) != 0) return 0; }
        else if (mm_jr_skip(&r) != 0) return 0;
    }
    if (more != 0 || mm_jr_end(&r) != 0) return 0;
    return epoch > 0 ? (int64_t)epoch : 0;
}
