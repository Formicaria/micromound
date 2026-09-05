/*
 * mm_envelope — one signed protocol message (PROTOCOL.md §2), built the way the host builds it.
 *
 * The canonical bytes are the whole envelope with "sig":"" — the field PRESENT and EMPTY, never
 * omitted (Envelope.CanonicalBytes in C#). The digest is sha256 over those bytes; the signature
 * is Ed25519 over those bytes; the signed wire form is the same bytes with the signature text
 * spliced into the empty sig. Because sig is the LAST field, the splice is an in-place append:
 * a device builds one buffer and never re-serializes.
 *
 *   canonical:  {"v":0,"id":…,"mound_id":…,"seq":N,"sent_at":…,"kind":…,"body":{…},"prev_digest":…,"sig":""}
 *   digest:     "sha256:" + 64 lowercase hex
 *   sig:        "ed25519:" + 128 lowercase hex
 *
 * Fixture: tests/Micromound.Tests/Golden/files/canonical-envelopes.txt.
 */
#ifndef MM_ENVELOPE_H
#define MM_ENVELOPE_H

#include <stddef.h>
#include <stdint.h>
#include "mm_json.h"

#ifdef __cplusplus
extern "C" {
#endif

#define MM_PROTOCOL_VERSION 0

#define MM_DIGEST_TEXT_LEN (7 + 64)        /* "sha256:" + hex, without the NUL */
#define MM_SIG_TEXT_LEN (8 + 128)          /* "ed25519:" + hex, without the NUL */

/* The kinds of the reduced profile (§8). A device emits the first four and receives the rest. */
#define MM_KIND_ENROLL "enroll"
#define MM_KIND_MOUND_SYNC "mound_sync"
#define MM_KIND_ACTION_RECORD "action_record"
#define MM_KIND_ACK "ack"
#define MM_KIND_CHARTER "charter"
#define MM_KIND_STOP "stop"

/* Writes the body as one JSON value into w. ctx is the caller's; see mm_bodies.h for the typed ones. */
typedef void (*mm_body_writer)(mm_json *w, const void *ctx);

typedef struct mm_envelope {
    const char *id;            /* a UUID string */
    const char *mound_id;
    long long seq;
    const char *sent_at;       /* yyyy-MM-ddTHH:mm:ssZ */
    const char *kind;
    const char *prev_digest;   /* "" for the chain anchor */
    mm_body_writer body;
    const void *body_ctx;
} mm_envelope;

/*
 * The canonical bytes into buf (NUL-terminated when there is room). Returns the length, or 0 on
 * failure; on overflow the needed length is in *needed when needed is not NULL.
 */
size_t mm_envelope_canonical(const mm_envelope *e, char *buf, size_t cap, size_t *needed);

/* "sha256:" + lowercase hex of sha256(canonical). out holds MM_DIGEST_TEXT_LEN + 1. */
void mm_envelope_digest(const char *canonical, size_t n, char out[MM_DIGEST_TEXT_LEN + 1]);

/* "ed25519:" + lowercase hex of the signature over canonical. out holds MM_SIG_TEXT_LEN + 1. */
void mm_envelope_sign(const char *canonical, size_t n, const uint8_t sk[64], char out[MM_SIG_TEXT_LEN + 1]);

/*
 * 0 when sig_text ("ed25519:<128 hex>") verifies over canonical under pk, -1 for anything else:
 * wrong algorithm, wrong length, bad hex, bad signature. Never throws, never guesses.
 */
int mm_envelope_verify(const char *canonical, size_t n, const char *sig_text, const uint8_t pk[32]);

/*
 * Turns canonical bytes (which end in "sig":""}) into the signed wire form by splicing sig_text
 * into the empty string, in place. buf must have room for n + MM_SIG_TEXT_LEN (+1 for the NUL).
 * Returns the new length, or 0 if the buffer is too small or the tail is not the canonical tail.
 */
size_t mm_envelope_splice_signature(char *buf, size_t n, size_t cap, const char *sig_text);

/*
 * Everything at once: canonical bytes, digest, signature, splice. Returns the signed wire length
 * or 0. digest_out (optional) receives the digest of the canonical bytes — the value the NEXT
 * envelope's prev_digest must carry; it is taken before the splice, since the digest never
 * covers the signature.
 */
size_t mm_envelope_write_signed(const mm_envelope *e, const uint8_t sk[64], char *buf, size_t cap,
                                char digest_out[MM_DIGEST_TEXT_LEN + 1]);

#ifdef __cplusplus
}
#endif

#endif
