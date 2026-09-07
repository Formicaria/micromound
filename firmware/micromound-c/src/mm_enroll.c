#include "mm_enroll.h"
#include "mm_format.h"
#include "mm_json.h"
#include "mm_json_read.h"
#include "mm_sha256.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static void set_str(char *dst, size_t cap, const char *src)
{
    size_t n = strlen(src);
    if (n >= cap) n = cap - 1;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

size_t mm_enroll_request_body(const mm_enroll_request *r, char *out, size_t cap)
{
    mm_json w;
    char pk_hex[65];
    size_t i;

    mm_hex_lower(r->device_public_key, 32, pk_hex);
    mm_json_init(&w, out, cap);
    mm_json_object_begin(&w);
    mm_json_kv_string(&w, "token", r->token);
    mm_json_kv_string(&w, "mound_id", r->mound_id);
    mm_json_kv_string(&w, "device_public_key", pk_hex);
    mm_json_kv_string(&w, "hardware_profile", r->hardware_profile ? r->hardware_profile : "");
    mm_json_kv_string(&w, "tier", r->tier);
    mm_json_key(&w, "capabilities");
    mm_json_array_begin(&w);
    for (i = 0; i < r->n_capabilities; i++) mm_json_string(&w, r->capabilities[i]);
    mm_json_array_end(&w);
    mm_json_kv_int(&w, "protocol_version", MM_PROTOCOL_VERSION);
    mm_json_key(&w, "driver_schemas");
    mm_json_array_begin(&w);
    mm_json_array_end(&w);
    /* features (v0.9.30): the named mission semantics this runtime implements. Empty here, and
       correctly so — a reduced-profile device never decodes a mission at all (PROTOCOL.md §8), so
       it implements none of them. Advertising the empty set is not a gap; it is the accurate answer,
       and it is what lets a controller tell a reduced device apart from an old full one. */
    mm_json_key(&w, "features");
    mm_json_array_begin(&w);
    mm_json_array_end(&w);
    mm_json_object_end(&w);
    return mm_json_finish(&w);
}

/* TryReadRefusalReason: the `reason` of a JSON body, or NULL when there is none to read. */
static const char *refusal_reason(const char *body, size_t n, char *out, size_t cap)
{
    mm_jr r;
    char key[32];
    int more, found = 0;
    size_t i, blank = 1;

    for (i = 0; i < n; i++) if (body[i] != ' ' && body[i] != '\t' && body[i] != '\n' && body[i] != '\r') blank = 0;
    if (blank) return NULL;

    mm_jr_init(&r, body, n);
    if (mm_jr_object_begin(&r) != 0) return NULL;
    while ((more = mm_jr_object_next(&r, key, sizeof key)) == 1) {
        if (strcmp(key, "reason") == 0) {
            if (mm_jr_peek(&r) == 'n') { if (mm_jr_null(&r) != 0) return NULL; continue; }
            if (mm_jr_string(&r, out, cap) != 0) return NULL;
            found = out[0] != '\0';
            for (i = 0; out[i]; i++) if (out[i] != ' ') break;
            if (out[i] == '\0') found = 0;
        } else if (mm_jr_skip(&r) != 0) {
            return NULL;
        }
    }
    if (more != 0 || mm_jr_end(&r) != 0) return NULL;
    return found ? out : NULL;
}

int mm_enroll_read_response(const mm_enroll_request *r, int status, const char *body, size_t n,
                            mm_enrollment *out, char *detail, size_t detail_cap)
{
    mm_jr jr;
    char key[32], pk_hex[128] = "";
    int more, has_pk = 0, all_zero = 1;
    size_t i, blank = 1;
    char reason[MM_REASON_CAP];

    memset(out, 0, sizeof *out);
    if (detail_cap) detail[0] = '\0';

    if (status >= 400 && status < 500) {
        const char *why = refusal_reason(body, n, reason, sizeof reason);
        if (why) snprintf(detail, detail_cap, "enrollment refused: HTTP %d \xe2\x80\x94 %s", status, why);
        else snprintf(detail, detail_cap, "enrollment refused: HTTP %d (token burned or unknown)", status);
        return 0;
    }
    if (status < 200 || status > 299) {
        snprintf(detail, detail_cap, "controller returned HTTP %d; enrollment not yet complete", status);
        return 0;
    }

    for (i = 0; i < n; i++) if (body[i] != ' ' && body[i] != '\t' && body[i] != '\n' && body[i] != '\r') blank = 0;
    if (blank) {
        set_str(detail, detail_cap, "controller response carried no controller_public_key");
        return 0;
    }

    mm_jr_init(&jr, body, n);
    if (mm_jr_object_begin(&jr) != 0) goto unreadable;
    while ((more = mm_jr_object_next(&jr, key, sizeof key)) == 1) {
        int rc;
        if (strcmp(key, "controller_public_key") == 0) {
            if (mm_jr_peek(&jr) == 'n') rc = mm_jr_null(&jr);
            else { rc = mm_jr_string(&jr, pk_hex, sizeof pk_hex); has_pk = rc == 0; }
        } else if (strcmp(key, "mound_id") == 0) {
            if (mm_jr_peek(&jr) == 'n') rc = mm_jr_null(&jr);
            else rc = mm_jr_string(&jr, out->controller_mound_id, sizeof out->controller_mound_id);
        } else if (strcmp(key, "sync_interval_s") == 0) {
            if (mm_jr_peek(&jr) == 'n') rc = mm_jr_null(&jr);
            else { rc = mm_jr_double(&jr, &out->sync_interval_s); out->has_sync_interval = rc == 0; }
        } else if (strcmp(key, "protocol_version") == 0) {
            if (mm_jr_peek(&jr) == 'n') rc = mm_jr_null(&jr);
            else { rc = mm_jr_int(&jr, &out->protocol_version); out->has_protocol_version = rc == 0; }
        } else if (strcmp(key, "colony_version") == 0) {
            if (mm_jr_peek(&jr) == 'n') rc = mm_jr_null(&jr);
            else rc = mm_jr_string(&jr, out->colony_version, sizeof out->colony_version);
        } else {
            rc = mm_jr_skip(&jr);
        }
        if (rc != 0) goto unreadable;
    }
    if (more != 0 || mm_jr_end(&jr) != 0) goto unreadable;

    for (i = 0; pk_hex[i]; i++) if (pk_hex[i] != ' ') break;
    if (!has_pk || pk_hex[i] == '\0') {
        set_str(detail, detail_cap, "controller response carried no controller_public_key");
        return 0;
    }
    if (strlen(pk_hex) % 2 != 0) goto unreadable;                     /* Convert.FromHexString throws FormatException */
    if (strlen(pk_hex) != 64 || mm_hex_parse(pk_hex, 32, out->controller_public_key) != 0) {
        uint8_t scratch[64];
        size_t bytes = strlen(pk_hex) / 2;
        if (bytes > sizeof scratch || mm_hex_parse(pk_hex, bytes, scratch) != 0) goto unreadable;
        snprintf(detail, detail_cap, "controller returned an invalid public key (%d bytes); not enrolled", (int)bytes);
        return 0;
    }
    for (i = 0; i < 32; i++) if (out->controller_public_key[i]) all_zero = 0;
    if (all_zero) {
        set_str(detail, detail_cap, "controller returned an invalid public key (32 bytes); not enrolled");
        return 0;
    }

    if (r->mound_id && r->mound_id[0] && out->controller_mound_id[0] && strcmp(r->mound_id, out->controller_mound_id) != 0) {
        snprintf(detail, detail_cap, "controller bound this key to mound '%s' but this device's manifest is '%s'; "
                 "the token was minted for a different mound \xe2\x80\x94 not enrolled", out->controller_mound_id, r->mound_id);
        return 0;
    }
    if (out->has_protocol_version && out->protocol_version != MM_PROTOCOL_VERSION) {
        snprintf(detail, detail_cap, "controller speaks protocol version %lld, this device speaks %d; not enrolled",
                 out->protocol_version, MM_PROTOCOL_VERSION);
        return 0;
    }
    if (out->has_sync_interval && !(isfinite(out->sync_interval_s) && out->sync_interval_s > 0)) out->has_sync_interval = 0;

    if (out->has_sync_interval) {
        char si[MM_FORMAT_DOUBLE_MAX];
        /* the host formats with "0.#": one decimal at most, no trailing ".0" */
        double v = out->sync_interval_s, rounded = floor(v * 10 + 0.5) / 10;
        if (rounded == floor(rounded)) snprintf(si, sizeof si, "%.0f", rounded);
        else snprintf(si, sizeof si, "%.1f", rounded);
        snprintf(detail, detail_cap, "enrolled (controller asks for a %ss sync cadence)", si);
    } else {
        set_str(detail, detail_cap, "enrolled");
    }
    return 1;

unreadable:
    set_str(detail, detail_cap, "enrollment response unreadable");
    return 0;
}

int mm_enroll(const mm_hal *hal, const mm_enroll_request *r, mm_enrollment *out, char *detail, size_t detail_cap)
{
    char body[2048], resp[4096];   /* 16 capabilities of MM_NAME_CAP fit with room */
    size_t n, resp_len = 0;
    int status = 0;

    n = mm_enroll_request_body(r, body, sizeof body);
    if (n == 0) { set_str(detail, detail_cap, "enrollment request does not fit its buffer"); return 0; }

    if (hal->http_post_json(hal->ctx, MM_ENROLL_PATH, body, n, resp, sizeof resp, &resp_len, &status) != 0) {
        set_str(detail, detail_cap, "controller unreachable; not enrolled yet");
        return -1;
    }
    if (!mm_enroll_read_response(r, status, resp, resp_len, out, detail, detail_cap)) return 0;

    /* persist before anything else happens: the key is what every future downlink is verified against */
    if (hal->kv_set(hal->ctx, MM_KV_CONTROLLER_PK, out->controller_public_key, 32) != 0) {
        set_str(detail, detail_cap, "enrolled, but the controller key could not be stored; not enrolled");
        return 0;
    }
    if (out->has_sync_interval) {
        char si[MM_FORMAT_DOUBLE_MAX];
        size_t sl = mm_format_double(out->sync_interval_s, si, sizeof si);
        if (sl) hal->kv_set(hal->ctx, MM_KV_SYNC_INTERVAL, (const uint8_t *)si, sl);
    }
    return 1;
}
