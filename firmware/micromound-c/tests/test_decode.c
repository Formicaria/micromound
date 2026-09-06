#include "mm_test.h"
#include "mm_decode.h"
#include "mm_json_read.h"

#include <string.h>

static const char MOUND[] = "mm-7f3a0000-0000-4000-8000-000000000001";
static const int64_t NOW = 1786741451LL;   /* 2026-08-14T21:04:11Z */

static const char GOLDEN_CHARTER[] =
    "{\"charter_id\":\"c0000000-0000-4000-8000-000000000001\",\"mound_id\":\"mm-7f3a0000-0000-4000-8000-000000000001\","
    "\"mission_ref\":\"mission-0001\",\"issued_at\":\"2026-08-14T21:04:11Z\",\"expires_at\":\"2026-08-14T22:04:11Z\","
    "\"lease_ttl_s\":900,\"action_ceiling\":\"benign\",\"capabilities\":[\"sense.temp\",\"act.relay_1\"],\"routines\":[],"
    "\"limits\":{\"act.relay_1\":{\"max_on_s\":30,\"min_off_s\":300,\"min\":null,\"max\":null,\"max_rate_per_h\":null}},"
    "\"evidence\":{\"required_for\":[\"act.*\"],\"min_interval_s\":60},\"safe_state\":\"all_actuators_off\",\"sync_interval_s\":15}";

static int has_reason(const mm_refusal *r, const char *reason)
{
    int i;
    for (i = 0; i < r->count && i < MM_REFUSAL_MAX; i++)
        if (strcmp(r->reasons[i], reason) == 0) return 1;
    return 0;
}

static int charter_from(const char *json, mm_charter_in *c)
{
    int err = 0;
    return mm_charter_parse(json, strlen(json), c, &err) == 0 ? 0 : err;
}

void test_decode(void)
{
    mm_charter_in c;
    mm_refusal why;
    int err;

    /* The golden charter, field for field. */
    CHECK(charter_from(GOLDEN_CHARTER, &c) == 0);
    CHECK_STR_EQ("c0000000-0000-4000-8000-000000000001", c.charter_id);
    CHECK_STR_EQ(MOUND, c.mound_id);
    CHECK_STR_EQ("mission-0001", c.mission_ref);
    CHECK_STR_EQ("2026-08-14T21:04:11Z", c.issued_at);
    CHECK_STR_EQ("2026-08-14T22:04:11Z", c.expires_at);
    CHECK(c.lease_ttl_s == 900);
    CHECK_STR_EQ("benign", c.action_ceiling);
    CHECK(c.n_capabilities == 2); CHECK_STR_EQ("sense.temp", c.capabilities[0]); CHECK_STR_EQ("act.relay_1", c.capabilities[1]);
    CHECK(c.n_routines == 0);
    CHECK(c.n_limits == 1); CHECK_STR_EQ("act.relay_1", c.limits[0].capability);
    CHECK(c.limits[0].limits.max_on_s.present && c.limits[0].limits.max_on_s.value == 30);
    CHECK(c.limits[0].limits.min_off_s.present && c.limits[0].limits.min_off_s.value == 300);
    CHECK(!c.limits[0].limits.min.present && !c.limits[0].limits.max.present && !c.limits[0].limits.max_rate_per_h.present);
    CHECK(c.n_evidence_required_for == 1); CHECK_STR_EQ("act.*", c.evidence_required_for[0]);
    CHECK(c.evidence_min_interval_s == 60);
    CHECK_STR_EQ("all_actuators_off", c.safe_state);
    CHECK(c.sync_interval_s == 15);

    /* …and it is accepted, with or without a device capability set. */
    {
        static const char *const caps[] = { "sense.temp", "act.relay_1", "act.relay_2" };
        static const char *const routines[] = { "routine.cool" };
        CHECK(mm_charter_validate(&c, MOUND, NOW, NULL, 0, NULL, 0, &why) == 0);
        CHECK(mm_charter_validate(&c, MOUND, NOW, caps, 3, routines, 1, &why) == 0);
        CHECK(mm_charter_validate(&c, MOUND, NOW, caps, 1, routines, 1, &why) == 1);
        CHECK(has_reason(&why, "capability 'act.relay_1' is not physically present on this device"));
    }

    /* Decode → re-encode is byte-identical: the cross-implementation property, on the receive side. */
    {
        mm_charter_view v;
        char out[1024];
        mm_json w;
        mm_charter_bind(&c, &v);
        mm_json_init(&w, out, sizeof out);
        mm_body_charter(&w, &v.charter);
        CHECK(mm_json_finish(&w) > 0);
        CHECK_STR_EQ(GOLDEN_CHARTER, out);
    }

    /* Tolerance the contract requires: whitespace, member order, unknown members, null for empty. */
    CHECK(charter_from(" { \"future_field\" : {\"x\":[1,2,{}]} , \"sync_interval_s\" : 20 , \"charter_id\" : \"c1\" , \"routines\" : null } ", &c) == 0);
    CHECK_STR_EQ("c1", c.charter_id); CHECK(c.sync_interval_s == 20); CHECK(c.n_routines == 0);
    CHECK_STR_EQ("observe", c.action_ceiling);              /* the C# defaults for what is absent */
    CHECK_STR_EQ("all_actuators_off", c.safe_state);
    CHECK(c.evidence_min_interval_s == 60 && c.lease_ttl_s == 0);

    /* Strictness the contract requires: types, capacities, lengths. Each a distinct error. */
    CHECK(charter_from("{\"lease_ttl_s\":\"900\"}", &c) == MM_JR_TYPE);
    CHECK(charter_from("{\"lease_ttl_s\":900.5}", &c) == MM_JR_RANGE);
    CHECK(charter_from("{\"capabilities\":\"act.relay_1\"}", &c) == MM_JR_TYPE);
    CHECK(charter_from("{\"limits\":{\"act.relay_1\":{\"max_on_s\":\"30\"}}}", &c) == MM_JR_TYPE);
    CHECK(charter_from("{\"limits\":[]}", &c) == MM_JR_TYPE);
    CHECK(charter_from("{\"charter_id\":\"" "0123456789012345678901234567890123456789012345678901234567890123" "\"}", &c) == MM_JR_OVERFLOW); /* 64 chars: one too many */
    CHECK(charter_from("{\"charter_id\":\"" "012345678901234567890123456789012345678901234567890123456789012" "\"}", &c) == 0);
    {
        char many[2048];
        int i;
        strcpy(many, "{\"capabilities\":[");
        for (i = 0; i < MM_MAX_CAPABILITIES + 1; i++) { strcat(many, i ? ",\"c\"" : "\"c\""); }
        strcat(many, "]}");
        CHECK(charter_from(many, &c) == MM_JR_TOO_MANY);
        strcpy(many, "{\"capabilities\":[");
        for (i = 0; i < MM_MAX_CAPABILITIES; i++) { strcat(many, i ? ",\"c\"" : "\"c\""); }
        strcat(many, "]}");
        CHECK(charter_from(many, &c) == 0 && c.n_capabilities == MM_MAX_CAPABILITIES);
    }
    CHECK(charter_from("{\"charter_id\":\"c1\"", &c) == MM_JR_TRUNCATED);
    CHECK(charter_from("{\"charter_id\":\"c1\"} trailing", &c) == MM_JR_TRAILING);
    CHECK(charter_from("[]", &c) == MM_JR_TYPE);
    CHECK(charter_from("", &c) == MM_JR_TRUNCATED);

    /* CharterValidator, reason for reason. */
    {
        mm_charter_in bad;
        CHECK(charter_from(GOLDEN_CHARTER, &bad) == 0);
        CHECK(mm_charter_validate(&bad, "mm-other", NOW, NULL, 0, NULL, 0, &why) == 1);
        CHECK(has_reason(&why, "mound_id mismatch: charter is for 'mm-7f3a0000-0000-4000-8000-000000000001', this mound is 'mm-other'"));

        CHECK(mm_charter_validate(&bad, MOUND, 1786745051LL, NULL, 0, NULL, 0, &why) == 1);       /* now == expires_at */
        CHECK(has_reason(&why, "charter already expired"));
        CHECK(mm_charter_validate(&bad, MOUND, 1786745050LL, NULL, 0, NULL, 0, &why) == 0);       /* one second before */

        strcpy(bad.action_ceiling, "hazardous");
        CHECK(mm_charter_validate(&bad, MOUND, NOW, NULL, 0, NULL, 0, &why) == 1);
        CHECK(has_reason(&why, "action_ceiling 'hazardous' is never a legal charter ceiling"));
        strcpy(bad.action_ceiling, "extreme");
        CHECK(mm_charter_validate(&bad, MOUND, NOW, NULL, 0, NULL, 0, &why) == 1);
        CHECK(has_reason(&why, "action_ceiling unknown: 'extreme'"));
        strcpy(bad.action_ceiling, "controlled");
        CHECK(mm_charter_validate(&bad, MOUND, NOW, NULL, 0, NULL, 0, &why) == 0);

        strcpy(bad.expires_at, "2026-08-14T21:00:00Z");                                         /* before issued_at, and past */
        CHECK(mm_charter_validate(&bad, MOUND, NOW, NULL, 0, NULL, 0, &why) == 2);
        CHECK(has_reason(&why, "charter already expired") && has_reason(&why, "expires_at precedes issued_at"));
        strcpy(bad.expires_at, "soon");
        CHECK(mm_charter_validate(&bad, MOUND, NOW, NULL, 0, NULL, 0, &why) == 1);
        CHECK(has_reason(&why, "expires_at unparseable: 'soon'"));
        strcpy(bad.expires_at, "2026-08-14T22:04:11Z");
        strcpy(bad.issued_at, "");
        CHECK(mm_charter_validate(&bad, MOUND, NOW, NULL, 0, NULL, 0, &why) == 1);
        CHECK(has_reason(&why, "issued_at unparseable: ''"));
        strcpy(bad.issued_at, "2026-08-14T21:04:11Z");

        bad.lease_ttl_s = 0; bad.sync_interval_s = -1; bad.safe_state[0] = '\0'; bad.charter_id[0] = '\0'; bad.mound_id[0] = '\0';
        CHECK(mm_charter_validate(&bad, MOUND, NOW, NULL, 0, NULL, 0, &why) == 5);
        {
            char joined[1024];
            CHECK_STR_EQ("charter_id missing; mound_id missing; lease_ttl_s must be positive; sync_interval_s must be positive; safe_state missing",
                         mm_refusal_join(&why, joined, sizeof joined));
        }
        CHECK(has_reason(&why, "lease_ttl_s must be positive") && has_reason(&why, "sync_interval_s must be positive") &&
              has_reason(&why, "safe_state missing") && has_reason(&why, "charter_id missing") && has_reason(&why, "mound_id missing"));

        CHECK(charter_from(GOLDEN_CHARTER, &bad) == 0);
        strcpy(bad.capabilities[1], "routine.cool");
        CHECK(mm_charter_validate(&bad, MOUND, NOW, NULL, 0, NULL, 0, &why) == 2);
        CHECK(has_reason(&why, "'routine.cool' is a routine and belongs in 'routines', not 'capabilities'"));
        CHECK(has_reason(&why, "limits key 'act.relay_1' matches no granted capability or routine"));     /* act.relay_1 no longer granted */

        CHECK(charter_from(GOLDEN_CHARTER, &bad) == 0);
        strcpy(bad.routines[0], "routine.unknown"); bad.n_routines = 1;
        {
            static const char *const routines[] = { "routine.cool" };
            CHECK(mm_charter_validate(&bad, MOUND, NOW, NULL, 0, routines, 1, &why) == 1);
            CHECK(has_reason(&why, "routine 'routine.unknown' is not registered on this device"));
            CHECK(mm_charter_validate(&bad, MOUND, NOW, NULL, 0, NULL, 0, &why) == 0);   /* no registry: not checked, as in C# */
        }
        /* a limit keyed to a routine is legal */
        strcpy(bad.limits[0].capability, "routine.unknown");
        CHECK(mm_charter_validate(&bad, MOUND, NOW, NULL, 0, NULL, 0, &why) == 0);
    }

    /* The small shared rules. */
    CHECK(mm_capability_pattern_matches("*", "anything"));
    CHECK(mm_capability_pattern_matches("act.*", "act.relay_1"));
    CHECK(!mm_capability_pattern_matches("act.*", "sense.temp"));
    CHECK(!mm_capability_pattern_matches("act.*", "act"));                  /* needs the dot */
    CHECK(mm_capability_pattern_matches("act.relay_1", "act.relay_1"));
    CHECK(!mm_capability_pattern_matches("act.relay_1", "act.relay_10"));
    CHECK(!mm_capability_pattern_matches("", "act.relay_1"));
    CHECK(!mm_capability_pattern_matches("act*", "act.relay_1"));          /* only ".*" is a glob */
    CHECK(mm_capability_is_routine("routine.cool") && !mm_capability_is_routine("act.routine"));
    CHECK(mm_action_class_parse("observe") == 0 && mm_action_class_parse("benign") == 1 &&
          mm_action_class_parse("controlled") == 2 && mm_action_class_parse("hazardous") == 3 &&
          mm_action_class_parse("Benign") == -1 && mm_action_class_parse("") == -1);

    /* stop and ack bodies. */
    {
        mm_stop_in stop;
        mm_ack_in ack;
        CHECK(mm_stop_parse("{\"reason\":\"operator stop\"}", 26, &stop, &err) == 0); CHECK_STR_EQ("operator stop", stop.reason);
        CHECK(mm_stop_parse("{}", 2, &stop, &err) == 0); CHECK_STR_EQ("", stop.reason);
        CHECK(mm_stop_parse("{\"reason\":1}", 12, &stop, &err) != 0 && err == MM_JR_TYPE);

        {
            static const char a[] = "{\"status\":\"ok\",\"refers_to\":\"11111111-1111-4111-8111-111111111111\",\"through_seq\":0,\"evidence_ids\":[\"e1\",\"e2\"],\"detail\":\"\"}";
            mm_ack_view v; char out[512]; mm_json w;
            CHECK(mm_ack_parse(a, sizeof a - 1, &ack, &err) == 0);
            CHECK_STR_EQ("ok", ack.status); CHECK(ack.through_seq == 0); CHECK(ack.n_evidence_ids == 2); CHECK_STR_EQ("e2", ack.evidence_ids[1]);
            mm_ack_bind(&ack, &v);
            mm_json_init(&w, out, sizeof out);
            mm_body_ack(&w, &v.ack);
            CHECK(mm_json_finish(&w) > 0);
            CHECK_STR_EQ(a, out);
        }
        CHECK(mm_ack_parse("{}", 2, &ack, &err) == 0);
        CHECK_STR_EQ("ok", ack.status); CHECK(ack.through_seq == -1); CHECK(ack.n_evidence_ids == 0);   /* AckBody defaults */
    }

    /* action_record: the golden body decodes and re-encodes byte for byte (a controller-side check, and
       the proof that a device's own records could be read back). */
    {
        static const char rec[] =
            "{\"action_id\":\"a0000000-0000-4000-8000-000000000001\",\"mission_id\":\"\",\"charter_id\":\"c0000000-0000-4000-8000-000000000001\","
            "\"capability\":\"act.relay_1\",\"routine_id\":\"\",\"requested_parameters\":{},\"parameters\":{\"on_s\":30},"
            "\"started_at\":\"2026-08-14T21:04:11Z\",\"ended_at\":\"2026-08-14T21:04:41Z\",\"outcome\":\"succeeded\",\"evidence_required\":false,"
            "\"evidence_refs\":[\"e0000000-0000-4000-8000-000000000001\"],\"evidence\":[],\"detail\":\"\"}";
        /* a device's record: the referenced reading rides inline (PROTOCOL.md §6), payload as an escaped string */
        static const char inl[] =
            "{\"action_id\":\"a-7\",\"mission_id\":\"\",\"charter_id\":\"\",\"capability\":\"sense.temp\",\"routine_id\":\"\","
            "\"requested_parameters\":{},\"parameters\":{},\"started_at\":\"2026-08-14T21:04:11Z\",\"ended_at\":\"2026-08-14T21:04:11Z\","
            "\"outcome\":\"succeeded\",\"evidence_required\":false,\"evidence_refs\":[\"e-sense.temp-1\"],"
            "\"evidence\":[{\"evidence_id\":\"e-sense.temp-1\",\"type\":\"reading\",\"captured_at\":\"2026-08-14T21:04:11Z\",\"source\":\"sense.temp\","
            "\"payload_json\":\"{\\\"value\\\":25,\\\"unit\\\":\\\"C\\\",\\\"capability\\\":\\\"sense.temp\\\"}\",\"content_digest\":\"\"}],\"detail\":\"\"}";
        mm_action_record_in in; mm_action_record_view v; char out[1024]; mm_json w;
        CHECK(mm_action_record_parse(rec, sizeof rec - 1, &in, &err) == 0);
        CHECK(in.n_parameters == 1); CHECK_STR_EQ("on_s", in.parameters[0].key); CHECK(in.parameters[0].value == 30);
        CHECK(in.n_requested_parameters == 0 && !in.evidence_required && in.n_evidence_refs == 1 && in.n_evidence == 0);
        mm_action_record_bind(&in, &v);
        mm_json_init(&w, out, sizeof out);
        mm_body_action_record(&w, &v.record);
        CHECK(mm_json_finish(&w) > 0);
        CHECK_STR_EQ(rec, out);
        CHECK(mm_action_record_parse(inl, sizeof inl - 1, &in, &err) == 0);
        CHECK(in.n_evidence == 1 && in.n_evidence_refs == 1);
        CHECK_STR_EQ("e-sense.temp-1", in.evidence[0].evidence_id);
        CHECK_STR_EQ("reading", in.evidence[0].type);
        CHECK_STR_EQ("{\"value\":25,\"unit\":\"C\",\"capability\":\"sense.temp\"}", in.evidence[0].payload_json);
        CHECK_STR_EQ("", in.evidence[0].content_digest);
        mm_action_record_bind(&in, &v);
        mm_json_init(&w, out, sizeof out);
        mm_body_action_record(&w, &v.record);
        CHECK(mm_json_finish(&w) > 0);
        CHECK_STR_EQ(inl, out);
        {   /* an item with an unknown member and a null array are read the way the host reads them */
            static const char odd[] = "{\"evidence\":[{\"evidence_id\":\"x\",\"future\":{\"a\":[1]}}]}";
            static const char nul[] = "{\"evidence\":null}";
            CHECK(mm_action_record_parse(odd, sizeof odd - 1, &in, &err) == 0 && in.n_evidence == 1);
            CHECK_STR_EQ("x", in.evidence[0].evidence_id); CHECK_STR_EQ("", in.evidence[0].type);
            CHECK(mm_action_record_parse(nul, sizeof nul - 1, &in, &err) == 0 && in.n_evidence == 0);
        }
        CHECK(mm_action_record_parse("{}", 2, &in, &err) == 0); CHECK_STR_EQ("unverified", in.outcome);
        {
            static const char nine[] = "{\"parameters\":{\"a\":1,\"b\":2,\"c\":3,\"d\":4,\"e\":5,\"f\":6,\"g\":7,\"h\":8,\"i\":9}}";
            CHECK(mm_action_record_parse(nine, sizeof nine - 1, &in, &err) != 0 && err == MM_JR_TOO_MANY);
        }
    }

    /* The envelope frame. */
    {
        static const char wire[] =
            "{\"v\":0,\"id\":\"11111111-1111-4111-8111-111111111111\",\"mound_id\":\"mm-7f3a0000-0000-4000-8000-000000000001\","
            "\"seq\":0,\"sent_at\":\"2026-08-14T21:04:11Z\",\"kind\":\"mound_sync\",\"body\":{\"state\":\"chartered\",\"uptime_s\":3600},"
            "\"prev_digest\":\"\",\"sig\":\"ed25519:00\"}";
        mm_envelope_in e;
        CHECK(mm_envelope_parse(wire, sizeof wire - 1, &e, &err) == 0);
        CHECK(e.v == 0 && e.seq == 0);
        CHECK_STR_EQ("11111111-1111-4111-8111-111111111111", e.id);
        CHECK_STR_EQ(MOUND, e.mound_id);
        CHECK_STR_EQ("mound_sync", e.kind);
        CHECK(e.body_len == 37 && strncmp(e.body, "{\"state\":\"chartered\",\"uptime_s\":3600}", e.body_len) == 0);
        CHECK_STR_EQ("", e.prev_digest);
        CHECK_STR_EQ("ed25519:00", e.sig);
        CHECK(mm_envelope_validate(&e, &why) == 0);

        e.v = 1; CHECK(mm_envelope_validate(&e, &why) == 1 && has_reason(&why, "unsupported protocol version 1")); e.v = 0;
        e.seq = -1; CHECK(mm_envelope_validate(&e, &why) == 1 && has_reason(&why, "seq negative")); e.seq = 0;
        strcpy(e.kind, "mission"); CHECK(mm_envelope_validate(&e, &why) == 1 && has_reason(&why, "refused_unknown_kind: 'mission'"));
        strcpy(e.kind, "evidence_bundle"); CHECK(mm_envelope_validate(&e, &why) == 1);        /* legal for a Pi, not here (§8) */
        strcpy(e.kind, "config"); CHECK(mm_envelope_validate(&e, &why) == 1);
        strcpy(e.kind, "stop"); CHECK(mm_envelope_validate(&e, &why) == 0);
        strcpy(e.sent_at, "yesterday"); CHECK(mm_envelope_validate(&e, &why) == 1 && has_reason(&why, "sent_at unparseable: 'yesterday'"));
        strcpy(e.sent_at, "2026-08-14T21:04:11Z");
        strcpy(e.id, "   "); e.mound_id[0] = '\0';
        CHECK(mm_envelope_validate(&e, &why) == 2 && has_reason(&why, "id missing") && has_reason(&why, "mound_id missing"));

        /* An absent v is the current version (the C# default); an absent body is "" (no body). */
        CHECK(mm_envelope_parse("{\"id\":\"x\"}", 10, &e, &err) == 0 && e.v == 0 && e.body_len == 0);
        CHECK(mm_envelope_parse("{\"seq\":\"1\"}", 11, &e, &err) != 0 && err == MM_JR_TYPE);
        CHECK(mm_envelope_parse("{\"body\":[1,}", 12, &e, &err) != 0 && err == MM_JR_SYNTAX);
    }
}
