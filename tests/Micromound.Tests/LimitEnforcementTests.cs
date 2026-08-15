using Micromound.Protocol;
using Micromound.Sim;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// SAFETY.md Layer 1 on the actuation path: it is not enough that <see cref="LimitClamp"/>
/// computes the right intersection — the mound has to actually pass every actuation through it.
/// </summary>
public class LimitEnforcementTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    private static Charter Benign(string moundId, CapabilityLimits? relayLimits = null)
    {
        var charter = new Charter
        {
            CharterId = Guid.NewGuid().ToString(),
            MoundId = moundId,
            MissionRef = "m1",
            IssuedAt = Now.ToWire(),
            ExpiresAt = Now.AddHours(6).ToWire(),
            LeaseTtlSeconds = 21600,
            ActionCeiling = "benign",
            Capabilities = ["sense.temp", "act.relay_1"],
            SafeState = "all_actuators_off"
        };

        if (relayLimits is not null) charter.Limits["act.relay_1"] = relayLimits;
        return charter;
    }

    private static SimMound Mound(CapabilityLimits firmware) => new("mm-1")
    {
        FirmwareLimits = new Dictionary<string, CapabilityLimits>(StringComparer.Ordinal)
        {
            ["act.relay_1"] = firmware
        }
    };

    [Fact]
    public void A_charter_cannot_widen_firmwares_on_time()
    {
        var mound = Mound(new CapabilityLimits { MaxOnSeconds = 30 });
        mound.OfferCharter(Benign("mm-1", new CapabilityLimits { MaxOnSeconds = 120 }), Now);

        var record = mound.Actuate("act.relay_1", Now, requestedOnSeconds: 120);

        Assert.Equal(ActionOutcomes.Clamped, record.Outcome);
        Assert.Equal(30d, record.Parameters["on_s"]);
        Assert.Contains("max_on_s", record.Detail);
    }

    [Fact]
    public void A_charter_that_narrows_is_obeyed()
    {
        var mound = Mound(new CapabilityLimits { MaxOnSeconds = 30 });
        mound.OfferCharter(Benign("mm-1", new CapabilityLimits { MaxOnSeconds = 5 }), Now);

        var record = mound.Actuate("act.relay_1", Now, requestedOnSeconds: 30);

        Assert.Equal(ActionOutcomes.Clamped, record.Outcome);
        Assert.Equal(5d, record.Parameters["on_s"]);
    }

    [Fact]
    public void A_request_inside_every_limit_is_not_clamped()
    {
        var mound = Mound(new CapabilityLimits { MaxOnSeconds = 30 });
        mound.OfferCharter(Benign("mm-1"), Now);

        var record = mound.Actuate("act.relay_1", Now, requestedOnSeconds: 10);

        Assert.Equal(ActionOutcomes.Succeeded, record.Outcome);
        Assert.Equal(10d, record.Parameters["on_s"]);
    }

    [Fact]
    public void The_duty_cycle_refuses_a_second_actuation_too_soon()
    {
        var mound = Mound(new CapabilityLimits { MaxOnSeconds = 5, MinOffSeconds = 300 });
        mound.OfferCharter(Benign("mm-1"), Now);

        Assert.Equal(ActionOutcomes.Succeeded, mound.Actuate("act.relay_1", Now, 5).Outcome);

        var tooSoon = mound.Actuate("act.relay_1", Now.AddSeconds(60), 5);
        Assert.Equal(ActionOutcomes.Refused, tooSoon.Outcome);
        Assert.Contains("min_off_s", tooSoon.Detail);

        // Once the off-time has elapsed, the same call is fine.
        Assert.Equal(ActionOutcomes.Succeeded, mound.Actuate("act.relay_1", Now.AddSeconds(400), 5).Outcome);
    }

    [Fact]
    public void A_charter_cannot_shorten_firmwares_required_off_time()
    {
        var mound = Mound(new CapabilityLimits { MaxOnSeconds = 5, MinOffSeconds = 300 });
        mound.OfferCharter(Benign("mm-1", new CapabilityLimits { MinOffSeconds = 1 }), Now);

        Assert.Equal(300d, mound.EffectiveLimits("act.relay_1").MinOffSeconds);

        mound.Actuate("act.relay_1", Now, 5);
        Assert.Equal(ActionOutcomes.Refused, mound.Actuate("act.relay_1", Now.AddSeconds(10), 5).Outcome);
    }

    [Fact]
    public void The_hourly_rate_limit_refuses_the_run_past_it()
    {
        var mound = Mound(new CapabilityLimits { MaxOnSeconds = 1, MaxRatePerHour = 3 });
        mound.OfferCharter(Benign("mm-1"), Now);

        for (var i = 0; i < 3; i++)
            Assert.Equal(ActionOutcomes.Succeeded, mound.Actuate("act.relay_1", Now.AddSeconds(i * 10), 1).Outcome);

        var over = mound.Actuate("act.relay_1", Now.AddSeconds(40), 1);
        Assert.Equal(ActionOutcomes.Refused, over.Outcome);
        Assert.Contains("max_rate_per_h", over.Detail);

        // The window is a trailing hour, not a calendar bucket.
        Assert.Equal(ActionOutcomes.Succeeded, mound.Actuate("act.relay_1", Now.AddHours(2), 1).Outcome);
    }

    [Fact]
    public void Every_refusal_is_queued_for_the_colony_not_swallowed()
    {
        var mound = new SimMound("mm-1");
        var refused = mound.Actuate("act.relay_1", Now); // no charter at all

        Assert.Equal(ActionOutcomes.Refused, refused.Outcome);
        Assert.NotEmpty(refused.Detail);

        var backlog = mound.DrainUplink();
        Assert.Contains(backlog, e => e.Kind == EnvelopeKinds.ActionRecord);
    }
}

/// <summary>
/// MICROMOUND.md design rule 3 — "Commands are not evidence" — as behaviour, not just a comment.
/// </summary>
public class EvidenceGateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    private static ActionRecord Succeeded(params string[] refs) => new()
    {
        ActionId = "a1",
        Capability = "act.relay_1",
        StartedAt = Now.ToWire(),
        EndedAt = Now.AddSeconds(5).ToWire(),
        Outcome = ActionOutcomes.Succeeded,
        EvidenceRefs = [.. refs]
    };

    private static EvidencePolicy RequireActs() =>
        new() { RequiredFor = ["act.*"], MinIntervalSeconds = 60 };

    private static Dictionary<string, EvidenceItem> Store(params EvidenceItem[] items) =>
        items.ToDictionary(i => i.EvidenceId, i => i, StringComparer.Ordinal);

    private static EvidenceItem Item(string id, DateTimeOffset capturedAt) => new()
    {
        EvidenceId = id,
        Type = "sensor_window",
        CapturedAt = capturedAt.ToWire(),
        Source = "sim.act.relay_1"
    };

    [Fact]
    public void An_action_with_no_evidence_is_unverified()
    {
        var outcome = EvidenceGate.Gate(Succeeded(), RequireActs(), Store(), Now, out var reason);

        Assert.Equal(ActionOutcomes.Unverified, outcome);
        Assert.Contains("no evidence", reason);
    }

    [Fact]
    public void An_action_whose_evidence_does_not_resolve_is_unverified()
    {
        var outcome = EvidenceGate.Gate(Succeeded("missing-id"), RequireActs(), Store(), Now, out var reason);

        Assert.Equal(ActionOutcomes.Unverified, outcome);
        Assert.Contains("missing", reason);
    }

    [Fact]
    public void Stale_evidence_does_not_prove_a_fresh_action()
    {
        var stale = Item("e1", Now.AddMinutes(-30));
        var outcome = EvidenceGate.Gate(Succeeded("e1"), RequireActs(), Store(stale), Now, out var reason);

        Assert.Equal(ActionOutcomes.Unverified, outcome);
        Assert.Contains("stale", reason);
    }

    [Fact]
    public void Evidence_from_the_future_is_not_believed()
    {
        var ahead = Item("e1", Now.AddMinutes(5));
        var outcome = EvidenceGate.Gate(Succeeded("e1"), RequireActs(), Store(ahead), Now, out _);

        Assert.Equal(ActionOutcomes.Unverified, outcome);
    }

    [Fact]
    public void Fresh_resolvable_evidence_keeps_the_outcome()
    {
        var fresh = Item("e1", Now);
        var outcome = EvidenceGate.Gate(Succeeded("e1"), RequireActs(), Store(fresh), Now, out var reason);

        Assert.Equal(ActionOutcomes.Succeeded, outcome);
        Assert.Equal("", reason);
    }

    [Fact]
    public void A_refusal_needs_no_proof()
    {
        var record = Succeeded();
        record.Outcome = ActionOutcomes.Refused;

        Assert.Equal(ActionOutcomes.Refused,
            EvidenceGate.Gate(record, RequireActs(), Store(), Now, out _));
    }

    [Fact]
    public void A_clamped_action_still_has_to_prove_itself()
    {
        var record = Succeeded();
        record.Outcome = ActionOutcomes.Clamped;

        Assert.Equal(ActionOutcomes.Unverified,
            EvidenceGate.Gate(record, RequireActs(), Store(), Now, out _));
    }

    [Theory]
    [InlineData("act.*", "act.relay_1", true)]
    [InlineData("act.*", "sense.temp", false)]
    [InlineData("*", "anything.at.all", true)]
    [InlineData("act.relay_1", "act.relay_1", true)]
    [InlineData("act.relay_1", "act.relay_2", false)]
    [InlineData("", "act.relay_1", false)]
    public void Capability_patterns_match_exactly_what_they_say(string pattern, string capability, bool expected)
    {
        Assert.Equal(expected, CapabilityPattern.Matches(pattern, capability));
    }

    [Fact]
    public void A_blind_mound_reports_unverified_rather_than_success()
    {
        var mound = new SimMound("mm-1") { SensorHealthy = false };
        mound.OfferCharter(new Charter
        {
            CharterId = "c1",
            MoundId = "mm-1",
            MissionRef = "m1",
            IssuedAt = Now.ToWire(),
            ExpiresAt = Now.AddHours(1).ToWire(),
            LeaseTtlSeconds = 900,
            ActionCeiling = "benign",
            Capabilities = ["act.relay_1"],
            Evidence = new EvidencePolicy { RequiredFor = ["act.*"], MinIntervalSeconds = 60 },
            SafeState = "all_actuators_off"
        }, Now);

        var record = mound.Actuate("act.relay_1", Now, requestedOnSeconds: 5);

        Assert.Equal(ActionOutcomes.Unverified, record.Outcome);
        Assert.Empty(record.EvidenceRefs);
        Assert.NotEmpty(record.Detail);
    }

    [Fact]
    public void A_sighted_mound_proves_its_own_actuation()
    {
        var mound = new SimMound("mm-1");
        mound.OfferCharter(new Charter
        {
            CharterId = "c1",
            MoundId = "mm-1",
            MissionRef = "m1",
            IssuedAt = Now.ToWire(),
            ExpiresAt = Now.AddHours(1).ToWire(),
            LeaseTtlSeconds = 900,
            ActionCeiling = "benign",
            Capabilities = ["act.relay_1"],
            Evidence = new EvidencePolicy { RequiredFor = ["act.*"], MinIntervalSeconds = 60 },
            SafeState = "all_actuators_off"
        }, Now);

        var record = mound.Actuate("act.relay_1", Now, requestedOnSeconds: 5);

        Assert.Equal(ActionOutcomes.Succeeded, record.Outcome);
        Assert.Single(record.EvidenceRefs);
        Assert.True(mound.Evidence.ContainsKey(record.EvidenceRefs[0]));
    }
}
