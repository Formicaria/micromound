/*
 * mm_frame — the Pi↔ESP32 link framing (PROTOCOL.md §12): the same HTTP-shaped exchanges a board
 * makes over Wi-Fi (POST a body to a path, get a status and a body back), carried over a byte
 * stream — a UART, USB-serial, a TCP socket — to a bridge that performs the HTTPS half.
 *
 *   frame = "MM" ver(1) type(1) seq(1) len(2, LE) payload(len) crc32(4, LE)
 *   crc32 = IEEE 802.3 / zlib CRC-32 over everything before it (magic to payload).
 *   ver   = MM_FRAME_VERSION. A frame of another version is dropped, never guessed at.
 *   type  = MM_FRAME_REQUEST  payload = path '\n' body           (device → bridge)
 *           MM_FRAME_RESPONSE payload = status(decimal) '\n' body (bridge → device); status 0 means the
 *                             bridge could not exchange at all — offline, exactly as the HAL defines it
 *   seq   = the device's request counter; a response echoes it, and a response for another seq is
 *           ignored (a late answer to a request the device gave up on).
 *
 * Nothing in the payload is interpreted here: the JSON inside is the signed protocol, untouched. The
 * bridge is transport, and a bridge that alters a byte produces an envelope that no longer verifies.
 * The decoder is incremental (feed it bytes as they arrive), resynchronises on the magic, and drops
 * anything that fails the CRC or exceeds its buffer. No allocation.
 */
#ifndef MM_FRAME_H
#define MM_FRAME_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MM_FRAME_VERSION 1
#define MM_FRAME_HEADER 7                  /* "MM" ver type seq len(2) */
#define MM_FRAME_TRAILER 4                 /* crc32 */
#define MM_FRAME_OVERHEAD (MM_FRAME_HEADER + MM_FRAME_TRAILER)

#define MM_FRAME_REQUEST 0x01
#define MM_FRAME_RESPONSE 0x02

#ifndef MM_FRAME_MAX_PAYLOAD
#define MM_FRAME_MAX_PAYLOAD 8192          /* a sync response: MM_LINK_RESPONSE_CAP */
#endif

/* IEEE 802.3 CRC-32 (the zlib polynomial, reflected, init and final xor 0xFFFFFFFF). */
uint32_t mm_crc32(uint32_t crc, const uint8_t *bytes, size_t n);   /* pass 0 to start */

/* Encodes one frame. Returns the frame length, or 0 when it does not fit or the payload is too large. */
size_t mm_frame_encode(uint8_t type, uint8_t seq, const uint8_t *payload, size_t n, uint8_t *out, size_t cap);

/* A request payload: path '\n' body. Returns the payload length or 0. */
size_t mm_frame_request_payload(const char *path, const char *body, size_t body_len, uint8_t *out, size_t cap);

/* A response payload: status '\n' body. Returns the payload length or 0. */
size_t mm_frame_response_payload(int status, const char *body, size_t body_len, uint8_t *out, size_t cap);

/* Splits a response payload. Returns 0, or -1 when there is no decimal status line. body may be empty. */
int mm_frame_parse_response(const uint8_t *payload, size_t n, int *status, const uint8_t **body, size_t *body_len);

/* Splits a request payload into path and body. Returns 0, or -1 when there is no '\n'. */
int mm_frame_parse_request(const uint8_t *payload, size_t n, const uint8_t **path, size_t *path_len,
                           const uint8_t **body, size_t *body_len);

/* The incremental decoder. */
typedef struct mm_frame_decoder {
    uint8_t buf[MM_FRAME_OVERHEAD + MM_FRAME_MAX_PAYLOAD];
    size_t n;                              /* bytes held */
    size_t expect;                         /* full frame length once the header is in; 0 before */
    /* the last completed frame; payload points into buf and is valid only until the next feed */
    uint8_t type, seq;
    const uint8_t *payload;
    size_t payload_len;
    /* counters, for the bridge's and the board's logs */
    unsigned frames, dropped_crc, dropped_version, dropped_oversize, resyncs;
} mm_frame_decoder;

void mm_frame_decoder_init(mm_frame_decoder *d);

/* Feeds one byte. Returns 1 when a frame completed (fields set), 0 otherwise. Bad frames are dropped
   and counted; the decoder then looks for the next magic. */
int mm_frame_feed(mm_frame_decoder *d, uint8_t byte);

#ifdef __cplusplus
}
#endif

#endif
