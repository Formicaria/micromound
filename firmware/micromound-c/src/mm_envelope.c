#include "mm_envelope.h"
#include "mm_ed25519.h"
#include "mm_sha256.h"

#include <string.h>

static const char CANONICAL_TAIL[] = "\"sig\":\"\"}";   /* the last 9 bytes of every canonical envelope */
#define CANONICAL_TAIL_LEN 9

size_t mm_envelope_canonical(const mm_envelope *e, char *buf, size_t cap, size_t *needed)
{
    mm_json w;
    size_t n;

    mm_json_init(&w, buf, cap);
    mm_json_object_begin(&w);
    mm_json_kv_int(&w, "v", MM_PROTOCOL_VERSION);
    mm_json_kv_string(&w, "id", e->id);
    mm_json_kv_string(&w, "mound_id", e->mound_id);
    mm_json_kv_int(&w, "seq", e->seq);
    mm_json_kv_string(&w, "sent_at", e->sent_at);
    mm_json_kv_string(&w, "kind", e->kind);
    mm_json_key(&w, "body");
    e->body(&w, e->body_ctx);
    mm_json_kv_string(&w, "prev_digest", e->prev_digest);
    mm_json_kv_string(&w, "sig", "");
    mm_json_object_end(&w);

    n = mm_json_finish(&w);
    if (needed) *needed = w.len;
    return n;
}

void mm_envelope_digest(const char *canonical, size_t n, char out[MM_DIGEST_TEXT_LEN + 1])
{
    uint8_t hash[MM_SHA256_DIGEST_LEN];
    mm_sha256_digest(canonical, n, hash);
    memcpy(out, "sha256:", 7);
    mm_hex_lower(hash, sizeof hash, out + 7);
}

void mm_envelope_sign(const char *canonical, size_t n, const uint8_t sk[64], char out[MM_SIG_TEXT_LEN + 1])
{
    uint8_t sig[MM_ED25519_SIGNATURE_LEN];
    mm_ed25519_sign(sig, (const uint8_t *)canonical, n, sk);
    memcpy(out, "ed25519:", 8);
    mm_hex_lower(sig, sizeof sig, out + 8);
}

int mm_envelope_verify(const char *canonical, size_t n, const char *sig_text, const uint8_t pk[32])
{
    uint8_t sig[MM_ED25519_SIGNATURE_LEN];

    if (sig_text == NULL || strlen(sig_text) != MM_SIG_TEXT_LEN) return -1;
    if (memcmp(sig_text, "ed25519:", 8) != 0) return -1;
    if (mm_hex_parse(sig_text + 8, sizeof sig, sig) != 0) return -1;
    return mm_ed25519_verify(sig, (const uint8_t *)canonical, n, pk);
}

size_t mm_envelope_splice_signature(char *buf, size_t n, size_t cap, const char *sig_text)
{
    size_t sl = strlen(sig_text);

    if (n < CANONICAL_TAIL_LEN || memcmp(buf + n - CANONICAL_TAIL_LEN, CANONICAL_TAIL, CANONICAL_TAIL_LEN) != 0)
        return 0;
    if (n + sl + 1 > cap) return 0;

    /* …"sig":"  +  <sig>  +  "}  — the signature text is hex and a fixed prefix, nothing to escape. */
    memcpy(buf + n - 2, sig_text, sl);
    buf[n - 2 + sl] = '"';
    buf[n - 1 + sl] = '}';
    buf[n + sl] = '\0';
    return n + sl;
}

size_t mm_envelope_write_signed(const mm_envelope *e, const uint8_t sk[64], char *buf, size_t cap,
                                char digest_out[MM_DIGEST_TEXT_LEN + 1])
{
    char sig[MM_SIG_TEXT_LEN + 1];
    size_t n = mm_envelope_canonical(e, buf, cap, NULL);
    if (n == 0) return 0;

    if (digest_out) mm_envelope_digest(buf, n, digest_out);
    mm_envelope_sign(buf, n, sk, sig);
    return mm_envelope_splice_signature(buf, n, cap, sig);
}
