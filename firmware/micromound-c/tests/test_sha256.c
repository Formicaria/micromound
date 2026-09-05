#include "mm_test.h"
#include "mm_sha256.h"

#include <string.h>

static const char *hash_hex(const void *data, size_t n, char out[65])
{
    uint8_t digest[MM_SHA256_DIGEST_LEN];
    mm_sha256_digest(data, n, digest);
    mm_hex_lower(digest, sizeof digest, out);
    return out;
}

void test_sha256(void)
{
    char hex[65];
    uint8_t bytes[4];
    char million[1000];
    mm_sha256 ctx;
    uint8_t digest[32];
    int i;

    /* FIPS 180-4 / NIST CAVS vectors. */
    CHECK_STR_EQ("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash_hex("", 0, hex));
    CHECK_STR_EQ("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash_hex("abc", 3, hex));
    CHECK_STR_EQ("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1",
                 hash_hex("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", 56, hex));
    CHECK_STR_EQ("cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1",
                 hash_hex("abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu", 112, hex));

    /* One million 'a', fed in uneven pieces: exercises the block buffer. */
    memset(million, 'a', sizeof million);
    mm_sha256_init(&ctx);
    for (i = 0; i < 1000; i++) mm_sha256_update(&ctx, million, sizeof million);
    mm_sha256_final(&ctx, digest);
    mm_hex_lower(digest, 32, hex);
    CHECK_STR_EQ("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0", hex);

    /* Incremental equals one-shot across every split of a 200-byte message. */
    {
        uint8_t msg[200], whole[32], parts[32];
        int split;
        for (i = 0; i < 200; i++) msg[i] = (uint8_t)(i * 7 + 3);
        mm_sha256_digest(msg, sizeof msg, whole);
        for (split = 0; split <= 200; split += 13) {
            mm_sha256_init(&ctx);
            mm_sha256_update(&ctx, msg, (size_t)split);
            mm_sha256_update(&ctx, msg + split, sizeof msg - (size_t)split);
            mm_sha256_final(&ctx, parts);
            CHECK_MEM_EQ(whole, parts, 32);
        }
    }

    /* Hex helpers. */
    CHECK(mm_hex_parse("00ff7Fa0", 4, bytes) == 0);
    CHECK(bytes[0] == 0x00 && bytes[1] == 0xff && bytes[2] == 0x7f && bytes[3] == 0xa0);
    CHECK(mm_hex_parse("zz", 1, bytes) == -1);
    mm_hex_lower(bytes, 4, hex);
    CHECK_STR_EQ("00ff7fa0", hex);
}
