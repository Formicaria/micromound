using System.Text;
using System.Text.Json;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// Freezes the exact bytes that go on the wire and the digests that chain them.
///
/// Everything here is fixed: no clocks, no GUIDs, no keys. Signatures are deliberately absent
/// because `sig` is excluded from the canonical bytes — that is precisely the property the C
/// mirror needs, since it lets a device sign and chain without re-serializing.
/// </summary>
public class CanonicalBytesTests
{
    private static readonly DateTimeOffset Fixed = DateTimeOffset.Parse("2026-08-14T21:04:11Z");

    private static Envelope Envelope(string id, long seq, string kind, object body, string prevDigest) => new()
    {
        Id = id,
        MoundId = "mm-7f3a0000-0000-4000-8000-000000000001",
        Seq = seq,
        SentAt = Fixed.AddSeconds(seq).ToWire(),
        Kind = kind,
        Body = JsonSerializer.SerializeToElement(body, ProtocolJson.Options),
        PrevDigest = prevDigest,
        Signature = "ed25519:not-covered-by-canonical-bytes"
    };

    private static Charter GoldenCharter() => new()
    {
        CharterId = "c0000000-0000-4000-8000-000000000001",
        MoundId = "mm-7f3a0000-0000-4000-8000-000000000001",
        MissionRef = "mission-0001",
        IssuedAt = Fixed.ToWire(),
        ExpiresAt = Fixed.AddHours(1).ToWire(),
        LeaseTtlSeconds = 900,
        ActionCeiling = "benign",
        Capabilities = ["sense.temp", "act.relay_1"],
        Limits = { ["act.relay_1"] = new CapabilityLimits { MaxOnSeconds = 30, MinOffSeconds = 300 } },
        Evidence = new EvidencePolicy { RequiredFor = ["act.*"], MinIntervalSeconds = 60 },
        SafeState = "all_actuators_off",
        SyncIntervalSeconds = 15
    };

    private static ActionRecord GoldenActionRecord() => new()
    {
        ActionId = "a0000000-0000-4000-8000-000000000001",
        CharterId = "c0000000-0000-4000-8000-000000000001",
        Capability = "act.relay_1",
        Parameters = { ["on_s"] = 30 },
        StartedAt = Fixed.ToWire(),
        EndedAt = Fixed.AddSeconds(30).ToWire(),
        Outcome = ActionOutcomes.Succeeded,
        EvidenceRefs = ["e0000000-0000-4000-8000-000000000001"],
        Detail = ""
    };

    private static EvidenceBundle GoldenEvidenceBundle() => new()
    {
        BundleId = "b0000000-0000-4000-8000-000000000001",
        Items =
        [
            new EvidenceItem
            {
                EvidenceId = "e0000000-0000-4000-8000-000000000001",
                Type = "sensor_window",
                CapturedAt = Fixed.AddSeconds(30).ToWire(),
                Source = "sim.act.relay_1",
                PayloadJson = """{"before":0,"after":1}""",
                ContentDigest = ""
            }
        ]
    };

    [Fact]
    public void Canonical_bytes_and_digests_are_frozen()
    {
        var report = new StringBuilder();
        report.AppendLine("# MICROMOUND canonical wire bytes — golden fixture");
        report.AppendLine("#");
        report.AppendLine("# Frozen by tests/Micromound.Tests/Golden/CanonicalBytesTests.cs.");
        report.AppendLine("# The M3 ESP32 C mirror must produce byte-identical output for the same input.");
        report.AppendLine("# `sig` is excluded from canonical bytes by construction — see Envelope.CanonicalBytes.");
        report.AppendLine();

        // A short chain, so the fixture pins prev_digest linkage and not just single envelopes.
        var previous = Append(report, Envelope("11111111-1111-4111-8111-111111111111", 0,
            EnvelopeKinds.MoundSync, new { state = "chartered", uptime_s = 3600 }, ""));

        previous = Append(report, Envelope("22222222-2222-4222-8222-222222222222", 1,
            EnvelopeKinds.ActionRecord, GoldenActionRecord(), previous));

        previous = Append(report, Envelope("33333333-3333-4333-8333-333333333333", 2,
            EnvelopeKinds.EvidenceBundle, GoldenEvidenceBundle(), previous));

        Append(report, Envelope("44444444-4444-4444-8444-444444444444", 3,
            EnvelopeKinds.Charter, GoldenCharter(), previous));

        GoldenFile.Verify("canonical-envelopes.txt", report.ToString());
    }

    [Fact]
    public void Bare_contract_serialization_is_frozen()
    {
        var report = new StringBuilder();
        report.AppendLine("# MICROMOUND contract bodies — golden fixture");
        report.AppendLine("#");
        report.AppendLine("# Field order, naming, and default emission for every typed body.");
        report.AppendLine();

        AppendBody(report, "charter", GoldenCharter());
        AppendBody(report, "action_record", GoldenActionRecord());
        AppendBody(report, "evidence_bundle", GoldenEvidenceBundle());

        GoldenFile.Verify("canonical-bodies.txt", report.ToString());
        return;

        static void AppendBody(StringBuilder into, string label, object body)
        {
            into.AppendLine($"## {label}");
            into.AppendLine(JsonSerializer.Serialize(body, ProtocolJson.Options));
            into.AppendLine();
        }
    }

    [Fact]
    public void A_round_trip_through_json_preserves_the_digest()
    {
        var envelope = Envelope("11111111-1111-4111-8111-111111111111", 7, EnvelopeKinds.ActionRecord,
            GoldenActionRecord(), "sha256:00");

        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        var restored = JsonSerializer.Deserialize<Envelope>(json, ProtocolJson.Options);

        Assert.NotNull(restored);
        Assert.Equal(envelope.Digest(), restored.Digest());
    }

    [Fact]
    public void The_signature_is_outside_the_digest()
    {
        var envelope = Envelope("11111111-1111-4111-8111-111111111111", 7, EnvelopeKinds.MoundSync,
            new { beat = 1 }, "");
        var before = envelope.Digest();

        envelope.Signature = "ed25519:" + new string('a', 128);

        Assert.Equal(before, envelope.Digest());
    }

    private static string Append(StringBuilder report, Envelope envelope)
    {
        var canonical = Encoding.UTF8.GetString(envelope.CanonicalBytes());
        var digest = envelope.Digest();

        report.AppendLine($"## seq {envelope.Seq} — {envelope.Kind}");
        report.AppendLine($"prev_digest: {(string.IsNullOrEmpty(envelope.PrevDigest) ? "(chain anchor)" : envelope.PrevDigest)}");
        report.AppendLine($"canonical:   {canonical}");
        report.AppendLine($"digest:      {digest}");
        report.AppendLine();

        return digest;
    }
}
