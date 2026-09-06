using System.Text.Json;
using Micromound.Capabilities;
using Micromound.Crypto;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The C device's side of the wire, checked by the host. <c>device-session.txt</c> is written by
/// <c>firmware/micromound-c/tests/test_device.c</c>: a reduced-profile device (<c>mm_device</c>)
/// running a whole session against a scripted controller — beats, a charter, actions, a stop,
/// refusals, an outage, a lease running out. Every <c>up:</c> line is an envelope the C code
/// signed and chained. This test is the controller's view of that session: every uplink envelope
/// must verify under the device key with the host's verifier, form one unbroken chain, pass the
/// host's envelope validation, and carry bodies the host's typed contracts decode and re-encode to
/// the same bytes. If the C device ever drifts from what a Pi sends, this is where it shows.
/// </summary>
public class DeviceSessionTests
{
    private static string FixturePath([System.Runtime.CompilerServices.CallerFilePath] string callerFile = "") =>
        Path.Combine(Path.GetDirectoryName(callerFile)!, "files", "device-session.txt");

    private sealed record Session(byte[] DevicePk, byte[] ControllerPk, List<Envelope> Uplink, List<string> UplinkRaw,
        List<Envelope> Downlink, List<string> DownlinkRaw, int LinkOutages);

    private static Session Load()
    {
        var path = FixturePath();
        Assert.True(File.Exists(path), $"device-session.txt is missing at {path}: run `make -C firmware/micromound-c test` once to write it");

        byte[]? devicePk = null, controllerPk = null;
        var uplink = new List<Envelope>();
        var uplinkRaw = new List<string>();
        var downlink = new List<Envelope>();
        var downlinkRaw = new List<string>();
        var outages = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("device_pk:", StringComparison.Ordinal)) devicePk = Convert.FromHexString(line["device_pk:".Length..].Trim());
            else if (line.StartsWith("controller_pk:", StringComparison.Ordinal)) controllerPk = Convert.FromHexString(line["controller_pk:".Length..].Trim());
            else if (line.StartsWith("up:", StringComparison.Ordinal))
            {
                var raw = line[3..].Trim();
                uplinkRaw.Add(raw);
                uplink.Add(JsonSerializer.Deserialize<Envelope>(raw, ProtocolJson.Options)!);
            }
            else if (line.StartsWith("down:", StringComparison.Ordinal))
            {
                var raw = line[5..].Trim();
                downlinkRaw.Add(raw);
                downlink.Add(JsonSerializer.Deserialize<Envelope>(raw, ProtocolJson.Options)!);
            }
            else if (line.StartsWith("link:", StringComparison.Ordinal)) outages++;
        }

        Assert.NotNull(devicePk);
        Assert.NotNull(controllerPk);
        return new Session(devicePk, controllerPk, uplink, uplinkRaw, downlink, downlinkRaw, outages);
    }

    private static bool Verifies(Envelope envelope, byte[] publicKey) =>
        SignatureFormat.TryDecode(envelope.Signature, out var algorithm, out var signature)
        && algorithm == SignatureFormat.Ed25519
        && Ed25519KeyPair.VerifyRaw(publicKey, envelope.CanonicalBytes(), signature);

    [Fact]
    public void Every_uplink_envelope_verifies_under_the_device_key_and_the_wire_form_is_canonical()
    {
        var session = Load();
        Assert.NotEmpty(session.Uplink);

        for (var i = 0; i < session.Uplink.Count; i++)
        {
            var envelope = session.Uplink[i];
            Assert.True(Verifies(envelope, session.DevicePk), $"uplink #{i} (seq {envelope.Seq}, {envelope.Kind}) does not verify under the device key");
            Assert.False(Verifies(envelope, session.ControllerPk), "an uplink envelope must not verify under the controller key");

            // What the C device put on the wire is exactly what the host would re-serialize: the
            // canonical bytes with the signature in place. A byte of drift here is a byte a Pi
            // would not produce.
            Assert.Equal(session.UplinkRaw[i], JsonSerializer.Serialize(envelope, ProtocolJson.Options));
            Assert.True(EnvelopeValidator.Validate(envelope, reducedProfile: true).IsValid,
                $"uplink #{i}: {string.Join("; ", EnvelopeValidator.Validate(envelope, reducedProfile: true).Errors)}");
            Assert.Contains(envelope.Kind, new[] { EnvelopeKinds.MoundSync, EnvelopeKinds.ActionRecord, EnvelopeKinds.Ack });
        }
    }

    [Fact]
    public void The_uplink_is_one_unbroken_chain_across_beats_outages_and_resends()
    {
        var session = Load();

        // The device re-sends what was not acknowledged (the outage), so the same seq appears more
        // than once; every copy must be byte-identical, and the distinct sequence must chain from
        // the anchor with no gap.
        var bySeq = session.Uplink.GroupBy(e => e.Seq).OrderBy(g => g.Key).ToList();
        foreach (var group in bySeq)
            Assert.Single(group.Select(e => JsonSerializer.Serialize(e, ProtocolJson.Options)).Distinct());

        var chain = bySeq.Select(g => g.First()).ToList();
        Assert.Equal(0, chain[0].Seq);
        Assert.Equal("", chain[0].PrevDigest);

        var keys = new InMemoryPublicKeyDirectory();
        keys.Register(chain[0].MoundId, session.DevicePk);
        var verifier = new Ed25519EnvelopeVerifier(keys);
        var result = EnvelopeValidator.ValidateChain(chain, "", verifier, chain[0].MoundId);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.True(session.LinkOutages >= 1, "the session is expected to include an outage");
        Assert.True(session.Uplink.Count > chain.Count, "the outage should have caused at least one re-send");
    }

    [Fact]
    public void Every_body_decodes_through_the_host_contracts_and_re_encodes_to_the_same_bytes()
    {
        var session = Load();
        var states = new HashSet<string>();
        var outcomes = new HashSet<string>();
        var ackStatuses = new HashSet<string>();

        foreach (var envelope in session.Uplink)
        {
            var body = envelope.Body.GetRawText();
            switch (envelope.Kind)
            {
                case EnvelopeKinds.MoundSync:
                    var state = envelope.Body.GetProperty("state").GetString()!;
                    Assert.Contains(state, new[] { MoundStates.Stopped, MoundStates.ObserveOnly, MoundStates.Quiesced, MoundStates.Chartered });
                    Assert.True(envelope.Body.GetProperty("queue_depth").GetInt64() >= 0);
                    states.Add(state);
                    break;

                case EnvelopeKinds.ActionRecord:
                    var record = JsonSerializer.Deserialize<ActionRecord>(body, ProtocolJson.Options)!;
                    Assert.Equal(body, JsonSerializer.Serialize(record, ProtocolJson.Options));
                    Assert.Contains(record.Outcome, new[] { ActionOutcomes.Succeeded, ActionOutcomes.Clamped, ActionOutcomes.Refused, ActionOutcomes.Stopped, ActionOutcomes.Unverified, ActionOutcomes.Failed });
                    Assert.True(ProtocolTime.IsCanonical(record.StartedAt) && ProtocolTime.IsCanonical(record.EndedAt));
                    outcomes.Add(record.Outcome);
                    if (record.Outcome == ActionOutcomes.Clamped)
                    {
                        // The device clamped 50 s to the narrowest tier, the charter's 30, and said so the way the host says it.
                        Assert.Equal(50, record.RequestedParameters["on_s"]);
                        Assert.Equal(30, record.Parameters["on_s"]);
                        Assert.Equal("'on_s' 50 -> 30 by max_on_s 30", record.Detail);
                        Assert.True(record.EvidenceRequired);
                        Assert.Single(record.EvidenceRefs);
                    }
                    if (record.Outcome == ActionOutcomes.Stopped)
                        Assert.Equal("stopped: a stop order is in force; stop precedes all work except observation", record.Detail);
                    if (record.Outcome == ActionOutcomes.Refused)
                        Assert.StartsWith("lease_expired: lease expired at ", record.Detail);
                    break;

                case EnvelopeKinds.Ack:
                    var ack = JsonSerializer.Deserialize<AckBody>(body, ProtocolJson.Options)!;
                    Assert.Equal(body, JsonSerializer.Serialize(ack, ProtocolJson.Options));
                    Assert.Contains(ack.Status, new[] { AckStatuses.Ok, AckStatuses.Refused, AckStatuses.RefusedUnknownKind });
                    Assert.Equal(-1, ack.ThroughSeq);                          // a device's acks answer downlink; they never advance the uplink window
                    Assert.False(string.IsNullOrEmpty(ack.RefersTo));
                    Assert.Contains(session.Downlink, d => d.Id == ack.RefersTo);   // every ack answers something the controller actually sent
                    ackStatuses.Add(ack.Status);
                    break;

                default:
                    Assert.Fail($"unexpected uplink kind {envelope.Kind}");
                    break;
            }
        }

        // The session walked the whole state machine and every ack status.
        Assert.Equal(new[] { MoundStates.Chartered, MoundStates.ObserveOnly, MoundStates.Quiesced, MoundStates.Stopped }.Order(), states.Order());
        Assert.Contains(ActionOutcomes.Clamped, outcomes);
        Assert.Contains(ActionOutcomes.Stopped, outcomes);
        Assert.Contains(ActionOutcomes.Refused, outcomes);
        Assert.Equal(new[] { AckStatuses.Ok, AckStatuses.Refused, AckStatuses.RefusedUnknownKind }.Order(), ackStatuses.Order());
    }

    [Fact]
    public void The_controllers_downlink_verifies_except_the_two_envelopes_the_script_corrupts()
    {
        var session = Load();
        var failing = session.Downlink.Where(d => !Verifies(d, session.ControllerPk)).ToList();
        var misrouted = session.Downlink.Where(d => d.MoundId != session.Uplink[0].MoundId).ToList();

        // Exactly one tampered charter (its lease_ttl_s edited after signing) fails to verify; exactly
        // one stop is addressed to another mound. Everything else the controller sent is good.
        Assert.Single(failing);
        Assert.Equal(EnvelopeKinds.Charter, failing[0].Kind);
        Assert.Single(misrouted);
        Assert.Equal(EnvelopeKinds.Stop, misrouted[0].Kind);
        Assert.Equal("mm-someone-else", misrouted[0].MoundId);

        // The device must have refused the action_record downhill loudly, and never have acknowledged the mission kind at all.
        Assert.Contains(session.Downlink, d => d.Kind == EnvelopeKinds.ActionRecord);
        Assert.Contains(session.Downlink, d => d.Kind == EnvelopeKinds.Mission);
        var acks = session.Uplink.Where(e => e.Kind == EnvelopeKinds.Ack)
            .Select(e => JsonSerializer.Deserialize<AckBody>(e.Body.GetRawText(), ProtocolJson.Options)!).ToList();
        var actionRecordDown = session.Downlink.Single(d => d.Kind == EnvelopeKinds.ActionRecord);
        var missionDown = session.Downlink.Single(d => d.Kind == EnvelopeKinds.Mission);
        Assert.Contains(acks, a => a.RefersTo == actionRecordDown.Id && a.Status == AckStatuses.RefusedUnknownKind);
        Assert.DoesNotContain(acks, a => a.RefersTo == missionDown.Id);
    }
}
