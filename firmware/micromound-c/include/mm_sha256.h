/*
 * mm_sha256 — FIPS 180-4 SHA-256, for envelope digests (PROTOCOL.md §2: "sha256:" + lowercase
 * hex of the canonical bytes). Incremental so a digest can be taken over a buffer as it is built.
 */
#ifndef MM_SHA256_H
#define MM_SHA256_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MM_SHA256_DIGEST_LEN 32

typedef struct mm_sha256 {
    uint32_t state[8];
    uint64_t total;                 /* bytes absorbed */
    uint8_t buffer[64];
    size_t buffered;
} mm_sha256;

void mm_sha256_init(mm_sha256 *ctx);
void mm_sha256_update(mm_sha256 *ctx, const void *data, size_t n);
void mm_sha256_final(mm_sha256 *ctx, uint8_t out[MM_SHA256_DIGEST_LEN]);

/* One-shot. */
void mm_sha256_digest(const void *data, size_t n, uint8_t out[MM_SHA256_DIGEST_LEN]);

/* Lowercase hex of n bytes into out (2n chars + NUL); out must hold 2n + 1. */
void mm_hex_lower(const uint8_t *bytes, size_t n, char *out);

/* Parses 2n hex digits (either case) into n bytes; returns 0 on success, -1 on a bad digit. */
int mm_hex_parse(const char *hex, size_t n, uint8_t *out);

#ifdef __cplusplus
}
#endif

#endif
