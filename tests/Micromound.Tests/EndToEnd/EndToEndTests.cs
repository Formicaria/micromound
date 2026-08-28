using Micromound.Protocol;
using Micromound.Runtime;
using Micromound.Sim;
using Micromound.Sync;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// Both ends of the wire at once: a mound composed the way a Pi will be composed —
/// drivers → kernel → ants → Mound Major → Runner — against a controller that verifies every
/// byte before believing any of it.
///
/// Everything before this file proves each rule in isolation. These prove the composition:
/// that a mission assigned over the wire runs through the same ants, produces the same records,
/// and comes back as the same report a local call would have produced — and that every
/// disconnection, restart, tamper, and stop lands the way the documents say it must.
/// </summary>
public class EndToEndTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    private const string Soil = "sense.soil_moisture";
    private const string Valve = "act.water_valve";

    private readonly SimController _controller = new();
    private readonly SimMound _mound;
    private readonly SimLink _link;

    public EndToEndTests()
    {
        _mound = new SimMound("mm-greenhouse-01")
        {
            DeviceCapabilities = new HashSet<string>(StringComparer.Ordinal)
            {
                Soil, "sense.temperature", Valve
            },
            FirmwareLimits = new Dictionary<string, CapabilityLimits>(StringComparer.Ordinal)
            {
                [Valve] = new CapabilityLimits { MaxOnSeconds = 30, MinOffSeconds = 300, MaxRatePerHour = 6 }
            }
        };

        _link = _mound.ConnectTo(_controller);

        // The fake world: dry soil that watering moistens.
        _mound.Sensor(Soil).Reading = 17;
        _mound.Sensor("sense.temperature").Reading = 24;
        _mound.Relay(Valve).OnActuated = seconds => _mound.Sensor(Soil).Reading += seconds * 1.5;
    }

    private Charter Charter(DateTimeOffset now, int ttl = 900) => new()
    {
        CharterId = "c-e2e",
        MoundId = _mound.MoundId,
        MissionRef = "greenhouse",
        IssuedAt = now.ToWire(),
        ExpiresAt = now.AddHours(2).ToWire(),
        LeaseTtlSeconds = ttl,
        ActionCeiling = "benign",
        Capabilities = [Soil, "sense.temperature", Valve],
        Limits = { [Valve] = new CapabilityLimits { MaxOnSeconds = 10 } },
        Evidence = new EvidencePolicy { RequiredFor = ["act.*"], MinIntervalSeconds = 60 },
        SafeState = "all_actuators_off"
    };

    private Mission Watering(DateTimeOffset now) => new()
    {
        MissionId = "ms-e2e",
        MoundId = _mound.MoundId,
        CharterId = "c-e2e",
        RequiredCapabilities = [Soil],
        RequiredEvidence = ["soil_before", "watering_action", "soil_after"],
        SafeState = "all_actuators_off",
        ExpiresAt = now.AddMinutes(30).ToWire(),
        Steps =
        [
            new MissionStep { StepId = "soil_before", Op = MissionStepOps.Sense, Capability = Soil,
                EvidenceTag = "soil_before" },
            new MissionStep { StepId = "water", Op = MissionStepOps.Act, Capability = Valve,
                Parameters = { ["on_s"] = 10 },
                Condition = new StepCondition { SourceStep = "soil_before", Op = ConditionOps.LessThan, Value = 20 },
                EvidenceTag = "watering_action" },
            new MissionStep { StepId = "soil_after", Op = MissionStepOps.Verify, Capability = Soil,
                Confirms = "water", EvidenceTag = "soil_after" }
        ]
    };

    private SimController.MoundAccount Account => _controller.Account(_mound.MoundId);

    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_documented_mission_runs_end_to_end_over_the_wire()
    {
        _controller.IssueCharter(Charter(Now), Now);
        _mound.Sync(Now);
        Assert.Equal("chartered", _mound.State);

        _controller.AssignMission(Watering(Now), Now.AddSeconds(10));
        _mound.Sync(Now.AddSeconds(10));      // receives and executes the mission
        _mound.Sync(Now.AddSeconds(20));      // delivers the records it produced

        // The controller's view, verified byte by byte on the way in.
        Assert.Equal(0, Account.Refusals);

        var report = Assert.Single(Account.Reports);
        Assert.Equal(MissionStates.Completed, report.State);
        Assert.Equal(3, report.Steps.Count);

        var watering = Account.Records.Single(r => r.Capability == Valve);
        Assert.Equal("ms-e2e", watering.MissionId);
        Assert.Equal(ActionOutcomes.Succeeded, watering.Outcome);
        Assert.Equal(10d, watering.Parameters["on_s"]);

        // Every evidence ref the record carries resolves at the controller — the claim travels
        // with its proof, or it is not a claim the controller believes.
        Assert.All(watering.EvidenceRefs, id => Assert.True(Account.Evidence.ContainsKey(id)));

        // And the fake physics happened: the soil the mission set out to water is wetter.
        Assert.True(_mound.Sensor(Soil).Reading > 20);
    }

    [Fact]
    public void Offline_work_queues_durably_and_reconnect_drains_with_the_chain_intact()
    {
        _controller.IssueCharter(Charter(Now), Now);
        _mound.Sync(Now);

        _link.Online = false;

        // Disconnected: already-authorized work continues; nothing is lost, nothing widens.
        var offline = _mound.Actuate(Valve, Now.AddSeconds(30), 5);
        Assert.Equal(ActionOutcomes.Succeeded, offline.Outcome);

        var failed = _mound.Sync(Now.AddSeconds(40));
        Assert.False(failed.Delivered);

        _link.Online = true;
        var drained = _mound.Sync(Now.AddSeconds(60));

        Assert.True(drained.Delivered);
        Assert.Equal(0, Account.Refusals);   // the chain held across the gap
        Assert.Contains(Account.Records, r => r.Capability == Valve && r.ActionId == offline.ActionId);
    }

    [Fact]
    public void A_stop_and_a_mission_in_the_same_batch_stop_first()
    {
        _controller.IssueCharter(Charter(Now), Now);
        _mound.Sync(Now);

        // The mission is queued BEFORE the stop, and would water if it ran. Stop is processed
        // ahead of all other downlink in the batch — PROTOCOL.md §7 — so it must not.
        _controller.AssignMission(Watering(Now), Now.AddSeconds(5));
        _controller.OrderStop(_mound.MoundId, "operator hit the button", Now.AddSeconds(6));

        _mound.Sync(Now.AddSeconds(10));     // stop lands first; the mission runs into it
        _mound.Sync(Now.AddSeconds(20));     // the stopped report and the stop ack ride up

        Assert.Equal("stopped", _mound.State);
        Assert.Equal(0, _mound.Relay(Valve).Actuations);

        // PROTOCOL.md §7 says "enter safe_state", and that means the drivers, not just the flag:
        // a stop that arrived over the wire must de-energize exactly like a local one.
        Assert.True(_mound.Relay(Valve).SafeStateEntries > 0);

        var report = Assert.Single(_controller.Account(_mound.MoundId).Reports
            .Where(r => r.MissionId == "ms-e2e"));
        Assert.Equal(MissionStates.Stopped, report.State);
    }

    [Fact]
    public void A_restart_mid_lease_revives_authority_without_extending_it()
    {
        _controller.IssueCharter(Charter(Now), Now);
        _mound.Sync(Now);
        var leaseExpiry = _mound.Authority.LeaseExpiresAt;

        var reborn = Restarted();
        reborn.Restore(Now.AddSeconds(300));

        Assert.Equal("chartered", reborn.State);
        Assert.Equal(leaseExpiry, reborn.Authority.LeaseExpiresAt);   // saved, never re-minted
        Assert.Equal(ActionOutcomes.Succeeded, reborn.Actuate(Valve, Now.AddSeconds(310), 5).Outcome);
    }

    [Fact]
    public void A_restart_never_clears_a_stop()
    {
        _controller.IssueCharter(Charter(Now), Now);
        _mound.Sync(Now);
        _mound.Stop();

        var reborn = Restarted();
        reborn.Restore(Now.AddSeconds(30));

        Assert.Equal("stopped", reborn.State);
        Assert.Equal(ActionOutcomes.Stopped, reborn.Actuate(Valve, Now.AddSeconds(40), 5).Outcome);
    }

    [Fact]
    public void A_restarted_mound_resumes_the_same_chain_the_controller_was_verifying()
    {
        _controller.IssueCharter(Charter(Now), Now);
        _mound.Sync(Now);

        _link.Online = false;
        _mound.Actuate(Valve, Now.AddSeconds(30), 5);   // queued, unsent, persisted

        var reborn = Restarted();
        reborn.Restore(Now.AddSeconds(60));
        reborn.ConnectTo(_controller);                   // reconnection, not re-enrollment

        var outcome = reborn.Sync(Now.AddSeconds(90));

        Assert.True(outcome.Delivered);
        Assert.Equal(0, Account.Refusals);               // the chain crossed the restart intact
        Assert.Contains(Account.Records, r => r.Capability == Valve);
    }

    [Fact]
    public void Tampered_uplink_is_dropped_audited_and_never_acknowledged()
    {
        _controller.IssueCharter(Charter(Now), Now);
        _mound.Sync(Now);
        var ackedBefore = Account.AckedSeq;

        // A man in the middle rewrites one envelope's body. The signature no longer matches the
        // canonical bytes, so the controller drops it — and everything after it breaks the chain,
        // because one forged envelope spoils an otherwise intact backlog.
        _link.Online = false;
        _mound.Actuate(Valve, Now.AddSeconds(30), 5);
        _link.Online = true;

        var tampering = new TamperingLink(_controller, tamperFromSeq: ackedBefore + 1);
        var tamperedOutcome = tampering.CarryOneSync(_mound, Now.AddSeconds(60));

        Assert.True(tamperedOutcome.Delivered);           // the wire worked; the content did not
        Assert.True(Account.Refusals > 0);
        Assert.Equal(ackedBefore, Account.AckedSeq);      // nothing tampered was ever acknowledged
        Assert.DoesNotContain(Account.Records, r => r.Capability == Valve);
    }

    [Fact]
    public void Downlink_the_controller_never_signed_is_dropped_and_audited()
    {
        // A charter body rewritten in flight fails signature verification at the mound: dropped,
        // audited, never processed — the mound stays observe-only rather than trusting the wire.
        _controller.IssueCharter(Charter(Now), Now);

        var tampering = new TamperingLink(_controller, tamperDownlinkKind: EnvelopeKinds.Charter);
        tampering.CarryOneSync(_mound, Now);

        Assert.Equal("observe_only", _mound.State);
        Assert.Contains(_mound.Runner.Audit, entry => entry.Contains("dropped"));
    }

    [Fact]
    public void An_unknown_downlink_kind_is_refused_loudly_with_an_ack()
    {
        // A well-signed envelope of a kind a mound never processes as downlink.
        _controller.QueueDownlink(_mound.MoundId, EnvelopeKinds.Enroll, new { }, Now);

        _mound.Sync(Now);
        _mound.Sync(Now.AddSeconds(10));   // the refusal ack rides the next beat

        Assert.Contains(Account.MoundAcks,
            ack => ack.Status == AckStatuses.RefusedUnknownKind);
    }

    [Fact]
    public void Acknowledged_evidence_becomes_evictable_and_unacknowledged_never_is()
    {
        _controller.IssueCharter(Charter(Now), Now);
        _mound.Sync(Now);

        _link.Online = false;
        _mound.Actuate(Valve, Now.AddSeconds(30), 5);
        Assert.NotEmpty(_mound.EvidenceStore.Pending());   // proof held while unacknowledged

        _link.Online = true;
        _mound.Sync(Now.AddSeconds(60));

        Assert.Empty(_mound.EvidenceStore.Pending());      // the controller has it; now evictable
    }

    [Fact]
    public void The_beat_renews_the_lease_and_silence_runs_it_down()
    {
        _controller.IssueCharter(Charter(Now, ttl: 900), Now);
        _mound.Sync(Now);

        // Connected: each acknowledged beat pushes expiry forward.
        _mound.Sync(Now.AddSeconds(800));
        Assert.True(_mound.LeaseAlive(Now.AddSeconds(1600)));

        // Disconnected: nothing on the device can extend it.
        _link.Online = false;
        _mound.Sync(Now.AddSeconds(1000));
        Assert.False(_mound.LeaseAlive(Now.AddSeconds(1701)));
        Assert.True(_mound.QuiesceIfExpired(Now.AddSeconds(1701)));
        Assert.Equal("quiesced", _mound.State);
    }

    // ---------------------------------------------------------------------------------------

    private SimMound Restarted() => new(_mound.MoundId)
    {
        Keys = _mound.Keys,
        Store = _mound.Store,
        DeviceCapabilities = _mound.DeviceCapabilities,
        FirmwareLimits = _mound.FirmwareLimits
    };

    /// <summary>
    /// A wire with a man in the middle. It carries one sync through a link that rewrites envelope
    /// bodies — uplink from a sequence number onward, or every downlink of a given kind — leaving
    /// each signature behind the truth it signed. The endpoints are untouched: what these tests
    /// prove is that both of them notice.
    /// </summary>
    private sealed class TamperingLink(SimController controller, long tamperFromSeq = long.MaxValue,
        string? tamperDownlinkKind = null) : ISyncTransport
    {
        private readonly SimLink _inner = new(controller);

        public SyncOutcome CarryOneSync(SimMound mound, DateTimeOffset now)
        {
            mound.UseTransport(this);
            try
            {
                return mound.Sync(now);
            }
            finally
            {
                mound.UseTransport(mound.Link);
            }
        }

        public bool TryExchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail)
        {
            if (uplink.Seq >= tamperFromSeq)
                uplink.Body = System.Text.Json.JsonSerializer.SerializeToElement(
                    new { tampered = true }, ProtocolJson.Options);

            var ok = _inner.TryExchange(uplink, out var received, out detail);

            if (tamperDownlinkKind is null)
            {
                downlink = received;
                return ok;
            }

            var mutated = new List<Envelope>();
            foreach (var envelope in received)
            {
                if (envelope.Kind == tamperDownlinkKind)
                    envelope.Body = System.Text.Json.JsonSerializer.SerializeToElement(
                        new { tampered = true }, ProtocolJson.Options);
                mutated.Add(envelope);
            }

            downlink = mutated;
            return ok;
        }
    }
}
