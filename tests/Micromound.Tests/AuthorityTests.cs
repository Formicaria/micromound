using Micromound.Protocol;
using Micromound.Sim;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The authority rules from MICROMOUND.md, proven on the simulated mound:
/// disconnection never widens authority, expiry quiesces, stop wins, resumption is explicit.
/// </summary>
public class AuthorityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    private static Charter Benign(string moundId) => new()
    {
        CharterId = Guid.NewGuid().ToString(),
        MoundId = moundId,
        MissionRef = "m1",
        IssuedAt = Now.ToString("O"),
        ExpiresAt = Now.AddHours(1).ToString("O"),
        LeaseTtlSeconds = 900,
        ActionCeiling = "benign",
        Capabilities = ["sense.temp", "act.relay_1"],
        SafeState = "all_actuators_off"
    };

    [Fact]
    public void No_charter_means_no_actuation()
    {
        var mound = new SimMound("mm-1");
        Assert.Equal("refused", mound.Actuate("act.relay_1", Now).Outcome);
    }

    [Fact]
    public void Chartered_actuation_succeeds_with_evidence()
    {
        var mound = new SimMound("mm-1");
        Assert.True(mound.OfferCharter(Benign("mm-1"), Now).IsValid);
        var record = mound.Actuate("act.relay_1", Now);
        Assert.Equal("succeeded", record.Outcome);
        Assert.NotEmpty(record.EvidenceRefs); // commands are not evidence
    }

    [Fact]
    public void Lease_expiry_quiesces_and_resumption_is_never_implicit()
    {
        var mound = new SimMound("mm-1");
        var charter = Benign("mm-1");
        mound.OfferCharter(charter, Now);

        var later = Now.AddSeconds(charter.LeaseTtlSeconds + 1);
        Assert.True(mound.QuiesceIfExpired(later));
        Assert.Equal("observe_only", mound.State);
        Assert.Equal("refused", mound.Actuate("act.relay_1", later).Outcome);

        // Renewal after expiry does nothing without a fresh charter.
        mound.RenewLease(later);
        Assert.Equal("refused", mound.Actuate("act.relay_1", later).Outcome);
    }

    [Fact]
    public void Connected_renewal_keeps_the_lease_alive()
    {
        var mound = new SimMound("mm-1");
        var charter = Benign("mm-1");
        mound.OfferCharter(charter, Now);
        var mid = Now.AddSeconds(charter.LeaseTtlSeconds - 10);
        mound.RenewLease(mid);
        var afterOriginalExpiry = Now.AddSeconds(charter.LeaseTtlSeconds + 10);
        Assert.False(mound.QuiesceIfExpired(afterOriginalExpiry));
        Assert.Equal("succeeded", mound.Actuate("act.relay_1", afterOriginalExpiry).Outcome);
    }

    [Fact]
    public void Stop_wins_over_a_valid_charter_and_needs_none()
    {
        var mound = new SimMound("mm-1");
        mound.OfferCharter(Benign("mm-1"), Now);
        mound.Stop();
        Assert.Equal("stopped", mound.State);
        Assert.Equal("stopped", mound.Actuate("act.relay_1", Now).Outcome);
    }

    [Fact]
    public void Capability_outside_charter_is_refused_even_when_device_has_it()
    {
        var mound = new SimMound("mm-1");
        var charter = Benign("mm-1");
        charter.Capabilities.Remove("act.relay_1");
        mound.OfferCharter(charter, Now);
        Assert.Equal("refused", mound.Actuate("act.relay_1", Now).Outcome);
    }

    [Fact]
    public void Observe_ceiling_refuses_actuation()
    {
        var mound = new SimMound("mm-1");
        var charter = Benign("mm-1");
        charter.ActionCeiling = "observe";
        mound.OfferCharter(charter, Now);
        Assert.Equal("refused", mound.Actuate("act.relay_1", Now).Outcome);
    }
}

public class EnvelopeChainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    [Fact]
    public void Offline_backlog_drains_with_an_intact_chain()
    {
        var mound = new SimMound("mm-1");
        for (var i = 0; i < 5; i++)
            mound.EnqueueUplink(EnvelopeKinds.MoundSync, new { beat = i }, Now.AddSeconds(i));

        var backlog = mound.DrainUplink();
        Assert.Equal(5, backlog.Count);
        Assert.True(EnvelopeValidator.ValidateChain(backlog, "").IsValid);
    }

    [Fact]
    public void Tampering_breaks_the_chain_detectably()
    {
        var mound = new SimMound("mm-1");
        for (var i = 0; i < 3; i++)
            mound.EnqueueUplink(EnvelopeKinds.MoundSync, new { beat = i }, Now.AddSeconds(i));

        var backlog = mound.DrainUplink().ToList();
        backlog[1].SentAt = Now.AddDays(1).ToString("O"); // tamper

        var result = EnvelopeValidator.ValidateChain(backlog, "");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_dropped_envelope_is_detected()
    {
        var mound = new SimMound("mm-1");
        for (var i = 0; i < 3; i++)
            mound.EnqueueUplink(EnvelopeKinds.MoundSync, new { beat = i }, Now.AddSeconds(i));

        var backlog = mound.DrainUplink().Where((_, i) => i != 1).ToList();
        var result = EnvelopeValidator.ValidateChain(backlog, "");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Reduced_profile_refuses_mission_and_evidence_kinds()
    {
        var mission = new Envelope
        {
            MoundId = "mm-1", Seq = 0, SentAt = Now.ToString("O"),
            Kind = EnvelopeKinds.Mission
        };
        Assert.True(EnvelopeValidator.Validate(mission).IsValid);
        var reduced = EnvelopeValidator.Validate(mission, reducedProfile: true);
        Assert.False(reduced.IsValid);
        Assert.Contains(reduced.Errors, e => e.Contains("refused_unknown_kind"));
    }

    [Fact]
    public void Unknown_kind_is_refused_loudly()
    {
        var envelope = new Envelope
        {
            MoundId = "mm-1", Seq = 0, SentAt = Now.ToString("O"),
            Kind = "mystery_kind"
        };
        var result = EnvelopeValidator.Validate(envelope);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("refused_unknown_kind"));
    }
}
