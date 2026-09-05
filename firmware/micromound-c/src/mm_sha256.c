#include "mm_sha256.h"

#include <string.h>

static const uint32_t K[64] = {
    0x428a2f98UL, 0x71374491UL, 0xb5c0fbcfUL, 0xe9b5dba5UL, 0x3956c25bUL, 0x59f111f1UL, 0x923f82a4UL, 0xab1c5ed5UL,
    0xd807aa98UL, 0x12835b01UL, 0x243185beUL, 0x550c7dc3UL, 0x72be5d74UL, 0x80deb1feUL, 0x9bdc06a7UL, 0xc19bf174UL,
    0xe49b69c1UL, 0xefbe4786UL, 0x0fc19dc6UL, 0x240ca1ccUL, 0x2de92c6fUL, 0x4a7484aaUL, 0x5cb0a9dcUL, 0x76f988daUL,
    0x983e5152UL, 0xa831c66dUL, 0xb00327c8UL, 0xbf597fc7UL, 0xc6e00bf3UL, 0xd5a79147UL, 0x06ca6351UL, 0x14292967UL,
    0x27b70a85UL, 0x2e1b2138UL, 0x4d2c6dfcUL, 0x53380d13UL, 0x650a7354UL, 0x766a0abbUL, 0x81c2c92eUL, 0x92722c85UL,
    0xa2bfe8a1UL, 0xa81a664bUL, 0xc24b8b70UL, 0xc76c51a3UL, 0xd192e819UL, 0xd6990624UL, 0xf40e3585UL, 0x106aa070UL,
    0x19a4c116UL, 0x1e376c08UL, 0x2748774cUL, 0x34b0bcb5UL, 0x391c0cb3UL, 0x4ed8aa4aUL, 0x5b9cca4fUL, 0x682e6ff3UL,
    0x748f82eeUL, 0x78a5636fUL, 0x84c87814UL, 0x8cc70208UL, 0x90befffaUL, 0xa4506cebUL, 0xbef9a3f7UL, 0xc67178f2UL
};

#define ROTR(x, n) (((x) >> (n)) | ((x) << (32 - (n))))
#define CH(x, y, z) (((x) & (y)) ^ (~(x) & (z)))
#define MAJ(x, y, z) (((x) & (y)) ^ ((x) & (z)) ^ ((y) & (z)))
#define BSIG0(x) (ROTR(x, 2) ^ ROTR(x, 13) ^ ROTR(x, 22))
#define BSIG1(x) (ROTR(x, 6) ^ ROTR(x, 11) ^ ROTR(x, 25))
#define SSIG0(x) (ROTR(x, 7) ^ ROTR(x, 18) ^ ((x) >> 3))
#define SSIG1(x) (ROTR(x, 17) ^ ROTR(x, 19) ^ ((x) >> 10))

static void compress(uint32_t state[8], const uint8_t block[64])
{
    uint32_t w[64], a, b, c, d, e, f, g, h;
    int t;

    for (t = 0; t < 16; t++)
        w[t] = ((uint32_t)block[t * 4] << 24) | ((uint32_t)block[t * 4 + 1] << 16)
             | ((uint32_t)block[t * 4 + 2] << 8) | (uint32_t)block[t * 4 + 3];
    for (t = 16; t < 64; t++)
        w[t] = SSIG1(w[t - 2]) + w[t - 7] + SSIG0(w[t - 15]) + w[t - 16];

    a = state[0]; b = state[1]; c = state[2]; d = state[3];
    e = state[4]; f = state[5]; g = state[6]; h = state[7];

    for (t = 0; t < 64; t++) {
        uint32_t t1 = h + BSIG1(e) + CH(e, f, g) + K[t] + w[t];
        uint32_t t2 = BSIG0(a) + MAJ(a, b, c);
        h = g; g = f; f = e; e = d + t1;
        d = c; c = b; b = a; a = t1 + t2;
    }

    state[0] += a; state[1] += b; state[2] += c; state[3] += d;
    state[4] += e; state[5] += f; state[6] += g; state[7] += h;
}

void mm_sha256_init(mm_sha256 *ctx)
{
    ctx->state[0] = 0x6a09e667UL; ctx->state[1] = 0xbb67ae85UL;
    ctx->state[2] = 0x3c6ef372UL; ctx->state[3] = 0xa54ff53aUL;
    ctx->state[4] = 0x510e527fUL; ctx->state[5] = 0x9b05688cUL;
    ctx->state[6] = 0x1f83d9abUL; ctx->state[7] = 0x5be0cd19UL;
    ctx->total = 0;
    ctx->buffered = 0;
}

void mm_sha256_update(mm_sha256 *ctx, const void *data, size_t n)
{
    const uint8_t *p = (const uint8_t *)data;
    ctx->total += n;

    if (ctx->buffered) {
        size_t take = 64 - ctx->buffered;
        if (take > n) take = n;
        memcpy(ctx->buffer + ctx->buffered, p, take);
        ctx->buffered += take;
        p += take;
        n -= take;
        if (ctx->buffered < 64) return;
        compress(ctx->state, ctx->buffer);
        ctx->buffered = 0;
    }
    while (n >= 64) {
        compress(ctx->state, p);
        p += 64;
        n -= 64;
    }
    if (n) {
        memcpy(ctx->buffer, p, n);
        ctx->buffered = n;
    }
}

void mm_sha256_final(mm_sha256 *ctx, uint8_t out[MM_SHA256_DIGEST_LEN])
{
    uint64_t bits = ctx->total * 8;
    uint8_t pad = 0x80;
    uint8_t zero = 0;
    uint8_t len[8];
    int i;

    mm_sha256_update(ctx, &pad, 1);
    while (ctx->buffered != 56) mm_sha256_update(ctx, &zero, 1);
    for (i = 0; i < 8; i++) len[i] = (uint8_t)(bits >> (56 - 8 * i));
    mm_sha256_update(ctx, len, 8);   /* total is now off by the padding, but it is no longer read */

    for (i = 0; i < 8; i++) {
        out[i * 4] = (uint8_t)(ctx->state[i] >> 24);
        out[i * 4 + 1] = (uint8_t)(ctx->state[i] >> 16);
        out[i * 4 + 2] = (uint8_t)(ctx->state[i] >> 8);
        out[i * 4 + 3] = (uint8_t)ctx->state[i];
    }
}

void mm_sha256_digest(const void *data, size_t n, uint8_t out[MM_SHA256_DIGEST_LEN])
{
    mm_sha256 ctx;
    mm_sha256_init(&ctx);
    mm_sha256_update(&ctx, data, n);
    mm_sha256_final(&ctx, out);
}

void mm_hex_lower(const uint8_t *bytes, size_t n, char *out)
{
    static const char hex[] = "0123456789abcdef";
    size_t i;
    for (i = 0; i < n; i++) {
        out[2 * i] = hex[bytes[i] >> 4];
        out[2 * i + 1] = hex[bytes[i] & 0xF];
    }
    out[2 * n] = '\0';
}

static int hex_value(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

int mm_hex_parse(const char *hex, size_t n, uint8_t *out)
{
    size_t i;
    for (i = 0; i < n; i++) {
        int hi = hex_value(hex[2 * i]), lo = hex_value(hex[2 * i + 1]);
        if (hi < 0 || lo < 0) return -1;
        out[i] = (uint8_t)((hi << 4) | lo);
    }
    return 0;
}
