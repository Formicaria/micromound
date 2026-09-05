#include "mm_test.h"
#include "mm_bodies.h"
#include "mm_ed25519.h"
#include "mm_envelope.h"
#include "mm_sha256.h"

#include <string.h>

/* The golden chain's first envelope (canonical-envelopes.txt, seq 0), with its anonymous body. */
static void write_sync_body(mm_json *w, const void *ctx)
{
    (void)ctx;
    mm_json_object_begin(w);
    mm_json_kv_string(w, "state", "chartered");
    mm_json_kv_int(w, "uptime_s", 3600);
    mm_json_object_end(w);
}

static const char SEQ0_CANONICAL[] =
    "{\"v\":0,\"id\":\"11111111-1111-4111-8111-111111111111\",\"mound_id\":\"mm-7f3a0000-0000-4000-8000-000000000001\","
    "\"seq\":0,\"sent_at\":\"2026-08-14T21:04:11Z\",\"kind\":\"mound_sync\",\"body\":{\"state\":\"chartered\",\"uptime_s\":3600},"
    "\"prev_digest\":\"\",\"sig\":\"\"}";
static const char SEQ0_DIGEST[] = "sha256:a7d0bbc72703cdac778eba6545e792992a9234f3c52fdc0cfd5bd214182316e3";

/*
 * Cross-implementation vector: the signature an independent RFC 8032 implementation (Python
 * `cryptography`, OpenSSL-backed) produced over SEQ0_CANONICAL with the seed 00 01 02 … 1f.
 * Ed25519 is deterministic, so the host's BouncyCastle produces the same bytes; a mound whose
 * seed happens to be this one would emit exactly this `sig`.
 */
static const char XIMPL_PK[] = "03a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1ddc8664125531b8";
static const char XIMPL_SIG[] = "ed25519:8eaea02e10a8b4212d155f5943acdd9f9467e86f4e8b6ac30018225c9f952fdd"
                               "f54ecce1e1f36e9e39f63a1a056819bf56e7fd2d27233c9e0e49f9758d34af00";

void test_envelope(void)
{
    mm_envelope e;
    char buf[1024];
    char digest[MM_DIGEST_TEXT_LEN + 1];
    char sig[MM_SIG_TEXT_LEN + 1];
    uint8_t seed[32], pk[32], sk[64], expected_pk[32];
    size_t n, needed, signed_len, i;

    memset(&e, 0, sizeof e);
    e.id = "11111111-1111-4111-8111-111111111111";
    e.mound_id = "mm-7f3a0000-0000-4000-8000-000000000001";
    e.seq = 0;
    e.sent_at = "2026-08-14T21:04:11Z";
    e.kind = MM_KIND_MOUND_SYNC;
    e.prev_digest = "";
    e.body = write_sync_body;

    /* Canonical bytes and digest, against the fixture. */
    n = mm_envelope_canonical(&e, buf, sizeof buf, &needed);
    CHECK(n == strlen(SEQ0_CANONICAL));
    CHECK(needed == n);
    CHECK_STR_EQ(SEQ0_CANONICAL, buf);
    mm_envelope_digest(buf, n, digest);
    CHECK_STR_EQ(SEQ0_DIGEST, digest);

    /* Too small: 0, and the needed length reported. */
    CHECK(mm_envelope_canonical(&e, buf, 100, &needed) == 0);
    CHECK(needed == strlen(SEQ0_CANONICAL));

    /* Sign with the cross-implementation seed; the bytes must match the other implementation. */
    for (i = 0; i < 32; i++) seed[i] = (uint8_t)i;
    mm_ed25519_seed_keypair(pk, sk, seed);
    CHECK(mm_hex_parse(XIMPL_PK, 32, expected_pk) == 0);
    CHECK_MEM_EQ(expected_pk, pk, 32);

    n = mm_envelope_canonical(&e, buf, sizeof buf, NULL);
    mm_envelope_sign(buf, n, sk, sig);
    CHECK_STR_EQ(XIMPL_SIG, sig);
    CHECK(mm_envelope_verify(buf, n, sig, pk) == 0);

    /* The signature text is parsed strictly. */
    CHECK(mm_envelope_verify(buf, n, "", pk) == -1);
    CHECK(mm_envelope_verify(buf, n, NULL, pk) == -1);
    CHECK(mm_envelope_verify(buf, n, sig + 1, pk) == -1);                       /* wrong length */
    {
        char other[MM_SIG_TEXT_LEN + 1];
        memcpy(other, sig, sizeof other);
        memcpy(other, "ed25518:", 8);                                            /* wrong algorithm */
        CHECK(mm_envelope_verify(buf, n, other, pk) == -1);
        memcpy(other, sig, sizeof other);
        other[20] = 'g';                                                         /* not hex */
        CHECK(mm_envelope_verify(buf, n, other, pk) == -1);
        memcpy(other, sig, sizeof other);
        other[20] = other[20] == '0' ? '1' : '0';                                /* a different signature */
        CHECK(mm_envelope_verify(buf, n, other, pk) == -1);
    }
    buf[10] ^= 1;                                                                /* a different message */
    CHECK(mm_envelope_verify(buf, n, sig, pk) == -1);
    buf[10] ^= 1;

    /* Splicing produces the wire form: canonical minus the empty sig plus the signature. */
    signed_len = mm_envelope_splice_signature(buf, n, sizeof buf, sig);
    CHECK(signed_len == n + strlen(sig));
    CHECK(memcmp(buf, SEQ0_CANONICAL, n - 3) == 0);
    CHECK(strncmp(buf + n - 2, sig, strlen(sig)) == 0);
    CHECK(buf[signed_len - 2] == '"' && buf[signed_len - 1] == '}' && buf[signed_len] == '\0');
    CHECK(strstr(buf, "\"sig\":\"ed25519:8eaea02e") != NULL);

    /* Splice refuses a buffer that is not a canonical envelope, or has no room. */
    CHECK(mm_envelope_splice_signature(buf, signed_len, sizeof buf, sig) == 0);   /* already spliced */
    n = mm_envelope_canonical(&e, buf, sizeof buf, NULL);
    CHECK(mm_envelope_splice_signature(buf, n, n + 10, sig) == 0);

    /* The all-in-one path agrees with the pieces, and reports the pre-splice digest. */
    {
        char whole[1024], whole_digest[MM_DIGEST_TEXT_LEN + 1];
        size_t wl = mm_envelope_write_signed(&e, sk, whole, sizeof whole, whole_digest);
        n = mm_envelope_canonical(&e, buf, sizeof buf, NULL);
        signed_len = mm_envelope_splice_signature(buf, n, sizeof buf, sig);
        CHECK(wl == signed_len);
        CHECK_STR_EQ(buf, whole);
        CHECK_STR_EQ(SEQ0_DIGEST, whole_digest);
        CHECK(mm_envelope_write_signed(&e, sk, whole, 300, NULL) == 0);      /* canonical fits, signature does not */
    }

    /* The typed bodies plug in as envelopes too, with a real chain: digest(n) is prev_digest(n+1). */
    {
        mm_mound_sync sync;
        mm_ack ack;
        static const char *const ids[] = { "e0000000-0000-4000-8000-000000000001" };
        mm_envelope next;
        char d0[MM_DIGEST_TEXT_LEN + 1], d1[MM_DIGEST_TEXT_LEN + 1], one[1024], two[1024];
        size_t l1, l2;

        sync.state = "observe_only";
        sync.queue_depth = 2;
        e.body = mm_body_mound_sync;
        e.body_ctx = &sync;
        l1 = mm_envelope_write_signed(&e, sk, one, sizeof one, d0);
        CHECK(l1 > 0);
        CHECK(strstr(one, "\"body\":{\"state\":\"observe_only\",\"queue_depth\":2},\"prev_digest\":\"\",\"sig\":\"ed25519:") != NULL);

        ack.status = "ok";
        ack.refers_to = "11111111-1111-4111-8111-111111111111";
        ack.through_seq = 0;
        ack.evidence_ids = ids;
        ack.n_evidence_ids = 1;
        ack.detail = "";
        next = e;
        next.id = "22222222-2222-4222-8222-222222222222";
        next.seq = 1;
        next.kind = MM_KIND_ACK;
        next.prev_digest = d0;
        next.body = mm_body_ack;
        next.body_ctx = &ack;
        l2 = mm_envelope_write_signed(&next, sk, two, sizeof two, d1);
        CHECK(l2 > 0);
        CHECK(strstr(two, "\"body\":{\"status\":\"ok\",\"refers_to\":\"11111111-1111-4111-8111-111111111111\",\"through_seq\":0,"
                          "\"evidence_ids\":[\"e0000000-0000-4000-8000-000000000001\"],\"detail\":\"\"},\"prev_digest\":\"sha256:") != NULL);
        CHECK(strncmp(strstr(two, "\"prev_digest\":\"") + 15, d0, MM_DIGEST_TEXT_LEN) == 0);
        CHECK(strcmp(d0, d1) != 0);
    }
}
