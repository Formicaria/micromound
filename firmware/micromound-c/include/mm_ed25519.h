/*
 * mm_ed25519 — the signing half of PROTOCOL.md §2/§3 for a constrained device.
 *
 * Pure Ed25519 (RFC 8032), byte-compatible with the host's BouncyCastle: the same 32-byte seed
 * produces the same public key and the same deterministic signature over the same message.
 * Backed by TweetNaCl (third_party/tweetnacl), reached through this header only, so the backend
 * can change without the callers noticing.
 *
 * There is no key generation here. A device's seed is generated once, by the device, from its
 * own entropy source, and stored in protected storage (SAFETY.md: nothing reads a seed back).
 * This library only rehydrates it.
 */
#ifndef MM_ED25519_H
#define MM_ED25519_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MM_ED25519_SEED_LEN 32
#define MM_ED25519_PUBLIC_KEY_LEN 32
#define MM_ED25519_SECRET_KEY_LEN 64      /* seed || public key, the NaCl layout */
#define MM_ED25519_SIGNATURE_LEN 64

/* Derives pk and the 64-byte sk (seed || pk) from a 32-byte seed. Never fails. */
void mm_ed25519_seed_keypair(uint8_t pk[MM_ED25519_PUBLIC_KEY_LEN],
                             uint8_t sk[MM_ED25519_SECRET_KEY_LEN],
                             const uint8_t seed[MM_ED25519_SEED_LEN]);

/* Detached signature over message; the message is read, never copied. */
void mm_ed25519_sign(uint8_t sig[MM_ED25519_SIGNATURE_LEN],
                     const uint8_t *message, size_t n,
                     const uint8_t sk[MM_ED25519_SECRET_KEY_LEN]);

/*
 * 0 when sig is a valid signature by pk over message, -1 otherwise. Rejects non-canonical
 * scalars (S >= L) as libsodium and BouncyCastle do, so a signature accepted here is accepted
 * by the host and vice versa. Constant-time in the comparison; not hardened beyond TweetNaCl.
 */
int mm_ed25519_verify(const uint8_t sig[MM_ED25519_SIGNATURE_LEN],
                      const uint8_t *message, size_t n,
                      const uint8_t pk[MM_ED25519_PUBLIC_KEY_LEN]);

#ifdef __cplusplus
}
#endif

#endif
