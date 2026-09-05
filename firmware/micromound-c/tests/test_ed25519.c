#include "mm_test.h"
#include "mm_ed25519.h"
#include "mm_sha256.h"

#include <string.h>

/* RFC 8032 §7.1 test vectors 1–3. */
static const struct {
    const char *seed, *pk, *msg, *sig;
} RFC[] = {
    { "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
      "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
      "",
      "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b" },
    { "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
      "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
      "72",
      "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00" },
    { "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
      "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
      "af82",
      "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a" }
};

void test_ed25519(void)
{
    size_t v;
    uint8_t seed[32], pk[32], sk[64], expected_pk[32], expected_sig[64], sig[64], msg[64];

    for (v = 0; v < sizeof RFC / sizeof RFC[0]; v++) {
        size_t n = strlen(RFC[v].msg) / 2;
        CHECK(mm_hex_parse(RFC[v].seed, 32, seed) == 0);
        CHECK(mm_hex_parse(RFC[v].pk, 32, expected_pk) == 0);
        CHECK(mm_hex_parse(RFC[v].sig, 64, expected_sig) == 0);
        CHECK(mm_hex_parse(RFC[v].msg, n, msg) == 0);

        mm_ed25519_seed_keypair(pk, sk, seed);
        CHECK_MEM_EQ(expected_pk, pk, 32);
        CHECK_MEM_EQ(seed, sk, 32);
        CHECK_MEM_EQ(pk, sk + 32, 32);

        mm_ed25519_sign(sig, msg, n, sk);
        CHECK_MEM_EQ(expected_sig, sig, 64);

        CHECK(mm_ed25519_verify(sig, msg, n, pk) == 0);
    }

    /* Round trip over a long, unaligned message — the incremental SHA-512 path with all its splits. */
    {
        uint8_t big[1000 + 37];
        size_t i;
        for (i = 0; i < sizeof big; i++) big[i] = (uint8_t)(i * 31 + 7);
        mm_ed25519_seed_keypair(pk, sk, seed);
        mm_ed25519_sign(sig, big, sizeof big, sk);
        CHECK(mm_ed25519_verify(sig, big, sizeof big, pk) == 0);

        big[500] ^= 1;
        CHECK(mm_ed25519_verify(sig, big, sizeof big, pk) == -1);
        big[500] ^= 1;

        sig[10] ^= 1;
        CHECK(mm_ed25519_verify(sig, big, sizeof big, pk) == -1);
        sig[10] ^= 1;

        sig[40] ^= 1;
        CHECK(mm_ed25519_verify(sig, big, sizeof big, pk) == -1);
        sig[40] ^= 1;

        pk[0] ^= 1;
        CHECK(mm_ed25519_verify(sig, big, sizeof big, pk) == -1);
        pk[0] ^= 1;
        CHECK(mm_ed25519_verify(sig, big, sizeof big, pk) == 0);
    }

    /* Non-canonical S (S + L, the classic malleated twin) is refused, as the host refuses it. */
    {
        static const uint8_t L[32] = {
            0xed, 0xd3, 0xf5, 0x5c, 0x1a, 0x63, 0x12, 0x58, 0xd6, 0x9c, 0xf7, 0xa2, 0xde, 0xf9, 0xde, 0x14,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x10 };
        unsigned carry = 0;
        int i;
        mm_ed25519_sign(sig, msg, 2, sk);
        CHECK(mm_ed25519_verify(sig, msg, 2, pk) == 0);
        for (i = 0; i < 32; i++) {
            unsigned s = (unsigned)sig[32 + i] + L[i] + carry;
            sig[32 + i] = (uint8_t)s;
            carry = s >> 8;
        }
        CHECK(mm_ed25519_verify(sig, msg, 2, pk) == -1);
    }

    /* A public key that is not a curve point is refused rather than trusted. */
    {
        uint8_t bad_pk[32];
        memset(bad_pk, 0xff, sizeof bad_pk);
        mm_ed25519_sign(sig, msg, 2, sk);
        CHECK(mm_ed25519_verify(sig, msg, 2, bad_pk) == -1);
    }
}
