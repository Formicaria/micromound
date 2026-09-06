#include "mm_frame.h"

#include <stdio.h>
#include <string.h>

/* ---- CRC-32 (IEEE 802.3, reflected 0xEDB88320), table built once ---------------------------- */

static uint32_t crc_table[256];
static int crc_ready = 0;

static void crc_init(void)
{
    uint32_t i, j, c;
    for (i = 0; i < 256; i++) {
        c = i;
        for (j = 0; j < 8; j++) c = (c & 1) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
        crc_table[i] = c;
    }
    crc_ready = 1;
}

uint32_t mm_crc32(uint32_t crc, const uint8_t *bytes, size_t n)
{
    size_t i;
    if (!crc_ready) crc_init();
    crc = ~crc;
    for (i = 0; i < n; i++) crc = crc_table[(crc ^ bytes[i]) & 0xFF] ^ (crc >> 8);
    return ~crc;
}

/* ---- encoding ------------------------------------------------------------------------------- */

size_t mm_frame_encode(uint8_t type, uint8_t seq, const uint8_t *payload, size_t n, uint8_t *out, size_t cap)
{
    uint32_t crc;
    size_t total = MM_FRAME_OVERHEAD + n;
    if (n > MM_FRAME_MAX_PAYLOAD || n > 0xFFFF || total > cap) return 0;
    out[0] = 'M'; out[1] = 'M';
    out[2] = MM_FRAME_VERSION;
    out[3] = type;
    out[4] = seq;
    out[5] = (uint8_t)(n & 0xFF);
    out[6] = (uint8_t)(n >> 8);
    if (n) memcpy(out + MM_FRAME_HEADER, payload, n);
    crc = mm_crc32(0, out, MM_FRAME_HEADER + n);
    out[MM_FRAME_HEADER + n + 0] = (uint8_t)(crc & 0xFF);
    out[MM_FRAME_HEADER + n + 1] = (uint8_t)((crc >> 8) & 0xFF);
    out[MM_FRAME_HEADER + n + 2] = (uint8_t)((crc >> 16) & 0xFF);
    out[MM_FRAME_HEADER + n + 3] = (uint8_t)((crc >> 24) & 0xFF);
    return total;
}

size_t mm_frame_request_payload(const char *path, const char *body, size_t body_len, uint8_t *out, size_t cap)
{
    size_t pl = strlen(path);
    if (pl == 0 || memchr(path, '\n', pl) != NULL) return 0;
    if (pl + 1 + body_len > cap) return 0;
    memcpy(out, path, pl);
    out[pl] = '\n';
    if (body_len) memcpy(out + pl + 1, body, body_len);
    return pl + 1 + body_len;
}

size_t mm_frame_response_payload(int status, const char *body, size_t body_len, uint8_t *out, size_t cap)
{
    char head[16];
    int hl;
    if (status < 0 || status > 999) return 0;
    hl = snprintf(head, sizeof head, "%d\n", status);
    if (hl <= 0 || (size_t)hl + body_len > cap) return 0;
    memcpy(out, head, (size_t)hl);
    if (body_len) memcpy(out + hl, body, body_len);
    return (size_t)hl + body_len;
}

int mm_frame_parse_response(const uint8_t *payload, size_t n, int *status, const uint8_t **body, size_t *body_len)
{
    size_t i;
    int value = 0;
    for (i = 0; i < n && i < 3; i++) {
        if (payload[i] == '\n') break;
        if (payload[i] < '0' || payload[i] > '9') return -1;
        value = value * 10 + (payload[i] - '0');
    }
    if (i == 0 || i >= n || payload[i] != '\n') return -1;
    *status = value;
    *body = payload + i + 1;
    *body_len = n - i - 1;
    return 0;
}

int mm_frame_parse_request(const uint8_t *payload, size_t n, const uint8_t **path, size_t *path_len,
                           const uint8_t **body, size_t *body_len)
{
    const uint8_t *nl = n ? (const uint8_t *)memchr(payload, '\n', n) : NULL;
    if (!nl || nl == payload) return -1;
    *path = payload;
    *path_len = (size_t)(nl - payload);
    *body = nl + 1;
    *body_len = n - *path_len - 1;
    return 0;
}

/* ---- decoding ------------------------------------------------------------------------------- */

void mm_frame_decoder_init(mm_frame_decoder *d)
{
    memset(d, 0, sizeof *d);
}

/* Drops the first byte and re-scans what is left for a magic: the stream may have been joined mid-frame. */
static void resync(mm_frame_decoder *d)
{
    size_t i, keep = 0;
    d->resyncs++;
    for (i = 1; i < d->n; i++)
        if (d->buf[i] == 'M' && (i + 1 == d->n || d->buf[i + 1] == 'M')) { keep = d->n - i; break; }
    if (keep) memmove(d->buf, d->buf + (d->n - keep), keep);
    d->n = keep;
    d->expect = 0;
}

int mm_frame_feed(mm_frame_decoder *d, uint8_t byte)
{
    if (d->n == 0) { if (byte == 'M') d->buf[d->n++] = byte; return 0; }   /* between frames: wait for the magic */
    if (d->n == 1) { if (byte == 'M') d->buf[d->n++] = byte; else d->n = 0; return 0; }
    if (d->n >= sizeof d->buf) resync(d);                                  /* cannot happen with a sane header; belt */
    d->buf[d->n++] = byte;

    /* a resync can leave more than a header's worth of bytes in the buffer, so both checks loop */
    for (;;) {
        if (d->n < 2) return 0;
        if (d->expect == 0 && d->n >= MM_FRAME_HEADER) {
            size_t len = (size_t)d->buf[5] | ((size_t)d->buf[6] << 8);
            if (d->buf[2] != MM_FRAME_VERSION) { d->dropped_version++; resync(d); continue; }
            if (len > MM_FRAME_MAX_PAYLOAD) { d->dropped_oversize++; resync(d); continue; }
            d->expect = MM_FRAME_OVERHEAD + len;
        }
        if (d->expect && d->n >= d->expect) {
            size_t body = d->expect - MM_FRAME_TRAILER;
            uint32_t want = (uint32_t)d->buf[body] | ((uint32_t)d->buf[body + 1] << 8) |
                            ((uint32_t)d->buf[body + 2] << 16) | ((uint32_t)d->buf[body + 3] << 24);
            if (mm_crc32(0, d->buf, body) != want) { d->dropped_crc++; resync(d); continue; }
            d->type = d->buf[3];
            d->seq = d->buf[4];
            d->payload = d->buf + MM_FRAME_HEADER;
            d->payload_len = body - MM_FRAME_HEADER;
            d->frames++;
            d->n = 0;
            d->expect = 0;
            return 1;
        }
        return 0;
    }
}
