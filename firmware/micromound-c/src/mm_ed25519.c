#include "mm_ed25519.h"

#include <stdlib.h>
#include <string.h>

/*
 * TweetNaCl is included, not linked: its field arithmetic and SHA-512 are file-static, and the
 * three functions below need them. The file compiles clean at -Wall; the -Wextra sign-compare
 * notes come from its FOR macro comparing signed loop counters with unsigned bounds, which is
 * harmless there and silenced here rather than by editing the vendored file.
 */
#if defined(__GNUC__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wsign-compare"
#endif
#include "tweetnacl.c"
#if defined(__GNUC__)
#pragma GCC diagnostic pop
#endif

/*
 * TweetNaCl's key generators call randombytes(). This library never generates a key — seeds come
 * from the device's own entropy at provisioning and from protected storage after that — so the
 * symbol exists only to satisfy the linker, and reaching it is a programming error.
 */
void randombytes(u8 *out, u64 n)
{
    (void)out;
    (void)n;
    abort();
}

/* ---- incremental SHA-512 over TweetNaCl's block function ------------------------------------ */

typedef struct {
    u8 h[64];
    u8 block[128];
    size_t buffered;
    u64 total;
} sha512_ctx;

static void sha512_init(sha512_ctx *c)
{
    memcpy(c->h, iv, 64);
    c->buffered = 0;
    c->total = 0;
}

static void sha512_update(sha512_ctx *c, const u8 *m, size_t n)
{
    c->total += n;
    if (c->buffered) {
        size_t take = 128 - c->buffered;
        if (take > n) take = n;
        memcpy(c->block + c->buffered, m, take);
        c->buffered += take;
        m += take;
        n -= take;
        if (c->buffered < 128) return;
        crypto_hashblocks(c->h, c->block, 128);
        c->buffered = 0;
    }
    if (n >= 128) {
        size_t whole = n & ~(size_t)127;
        crypto_hashblocks(c->h, m, whole);
        m += whole;
        n -= whole;
    }
    if (n) {
        memcpy(c->block, m, n);
        c->buffered = n;
    }
}

static void sha512_final(sha512_ctx *c, u8 out[64])
{
    /* The same padding crypto_hash applies, over what is still buffered. */
    u8 x[256];
    size_t n = c->buffered, pad;
    memset(x, 0, sizeof x);
    memcpy(x, c->block, n);
    x[n] = 128;
    pad = 256 - 128 * (n < 112);
    x[pad - 9] = (u8)(c->total >> 61);
    ts64(x + pad - 8, c->total << 3);
    crypto_hashblocks(c->h, x, pad);
    memcpy(out, c->h, 64);
}

/* ---- Ed25519, detached ------------------------------------------------------------------- */

static void clamp(u8 d[64])
{
    d[0] &= 248;
    d[31] &= 127;
    d[31] |= 64;
}

void mm_ed25519_seed_keypair(uint8_t pk[32], uint8_t sk[64], const uint8_t seed[32])
{
    u8 d[64];
    gf p[4];

    crypto_hash(d, seed, 32);
    clamp(d);
    scalarbase(p, d);
    pack(pk, p);

    memcpy(sk, seed, 32);
    memcpy(sk + 32, pk, 32);
}

void mm_ed25519_sign(uint8_t sig[64], const uint8_t *message, size_t n, const uint8_t sk[64])
{
    u8 d[64], h[64], r[64];
    i64 i, j, x[64];
    gf p[4];
    sha512_ctx c;

    crypto_hash(d, sk, 32);
    clamp(d);

    /* r = H(prefix || M) */
    sha512_init(&c);
    sha512_update(&c, d + 32, 32);
    sha512_update(&c, message, n);
    sha512_final(&c, r);
    reduce(r);

    /* R = rB */
    scalarbase(p, r);
    pack(sig, p);

    /* h = H(R || A || M) */
    sha512_init(&c);
    sha512_update(&c, sig, 32);
    sha512_update(&c, sk + 32, 32);
    sha512_update(&c, message, n);
    sha512_final(&c, h);
    reduce(h);

    /* S = r + h·a (mod L) */
    FOR(i, 64) x[i] = 0;
    FOR(i, 32) x[i] = (u64)r[i];
    FOR(i, 32) FOR(j, 32) x[i + j] += h[i] * (u64)d[j];
    modL(sig + 32, x);
}

/* True when the 32-byte little-endian scalar s is below the group order L. */
static int scalar_is_canonical(const u8 s[32])
{
    int i;
    for (i = 31; i >= 0; i--) {
        if (s[i] < (u8)L[i]) return 1;
        if (s[i] > (u8)L[i]) return 0;
    }
    return 0; /* equal to L */
}

int mm_ed25519_verify(const uint8_t sig[64], const uint8_t *message, size_t n, const uint8_t pk[32])
{
    u8 t[32], h[64];
    gf p[4], q[4];
    sha512_ctx c;

    if (!scalar_is_canonical(sig + 32)) return -1;
    if (unpackneg(q, pk)) return -1;

    sha512_init(&c);
    sha512_update(&c, sig, 32);
    sha512_update(&c, pk, 32);
    sha512_update(&c, message, n);
    sha512_final(&c, h);
    reduce(h);

    scalarmult(p, q, h);
    scalarbase(q, sig + 32);
    add(p, q);
    pack(t, p);

    return crypto_verify_32(sig, t) ? -1 : 0;
}
