using Micromound.Capabilities;
using Micromound.Evidence;
using Micromound.Protocol;
using Micromound.Runtime;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The local evidence store — retention, and the one rule that overrides capacity.
/// </summary>
public class EvidenceStoreTests
{
    private static DateTimeOffset Now => KernelHarness.Now;

    private static EvidenceItem Item(string id, double value = 1) =>
        EvidenceReadings.Create(id, "sense.soil_moisture", value, Now);

    [Fact]
    public void An_item_round_trips_by_id()
    {
        var store = new InMemoryEvidenceStore();
        store.Add(Item("e1", 17));

        Assert.True(store.TryGet("e1", out var found));
        Assert.True(EvidenceReadings.TryRead(found, out var value));
        Assert.Equal(17, value);
    }

    [Fact]
    public void Pending_is_what_the_controller_has_not_acknowledged()
    {
        var store = new InMemoryEvidenceStore();
        store.Add(Item("e1"));
        store.Add(Item("e2"));

        store.Acknowledge(["e1"]);

        Assert.Single(store.Pending());
        Assert.Equal("e2", store.Pending()[0].EvidenceId);
    }

    [Fact]
    public void Capacity_evicts_the_oldest_acknowledged_item_and_says_how_many()
    {
        var store = new InMemoryEvidenceStore(capacity: 2);
        store.Add(Item("e1"));
        store.Add(Item("e2"));
        store.Acknowledge(["e1", "e2"]);

        store.Add(Item("e3"));

        Assert.False(store.TryGet("e1", out _));
        Assert.True(store.TryGet("e3", out _));
        Assert.Equal(1, store.TakeEvictedCount());
    }

    /// <remarks>
    /// The rule that overrides capacity. Silently dropping proof the controller has never seen is
    /// indistinguishable from never having captured it, so the store exceeds its bound instead.
    /// </remarks>
    [Fact]
    public void Unacknowledged_proof_is_never_evicted()
    {
        var store = new InMemoryEvidenceStore(capacity: 2);
        store.Add(Item("e1"));
        store.Add(Item("e2"));
        store.Add(Item("e3"));

        Assert.Equal(3, store.Count);
        Assert.True(store.TryGet("e1", out _));
        Assert.Equal(0, store.TakeEvictedCount());
    }

    /// <summary>It rides out on the next bundle as `evicted_acked_items`, and is not repeated.</summary>
    [Fact]
    public void The_evicted_count_is_reported_once()
    {
        var store = new InMemoryEvidenceStore(capacity: 1);
        store.Add(Item("e1"));
        store.Acknowledge(["e1"]);
        store.Add(Item("e2"));

        Assert.Equal(1, store.TakeEvictedCount());
        Assert.Equal(0, store.TakeEvictedCount());
    }

    /// <remarks>
    /// A correlator that swept up every reading captured near an action would let the mound
    /// nominate its own corroboration, and "commands are not evidence" means very little if a
    /// device may decide after the fact which observations happen to support it.
    /// </remarks>
    [Fact]
    public void A_correlator_resolves_only_the_refs_the_record_carries()
    {
        var store = new InMemoryEvidenceStore();
        store.Add(Item("cited"));
        store.Add(Item("nearby-but-unnamed"));

        var record = new ActionRecord { ActionId = "a1", EvidenceRefs = { "cited" } };
        var view = new EvidenceCorrelator(store).For(record, new EvidencePolicy(), Now);

        Assert.Single(view);
        Assert.True(view.ContainsKey("cited"));
    }

    [Fact]
    public void A_ref_that_does_not_resolve_is_absent_rather_than_stubbed()
    {
        var record = new ActionRecord { ActionId = "a1", EvidenceRefs = { "ghost" } };

        var view = new EvidenceCorrelator(new InMemoryEvidenceStore()).For(record, new EvidencePolicy(), Now);

        Assert.Empty(view);
    }
}

/// <summary>
/// Verification — the second half of `SENSE → ACT → SENSE AGAIN → VERIFY`, which until now could
/// not affect any outcome.
///
/// The evidence gate fired exactly once, inside the kernel, at the moment of execution. The
/// confirming reading arrived afterwards and nothing revisited the action, so a `verify` step was
/// indistinguishable from a `sense` step in every line of code in the repository. These tests are
/// what makes the difference real.
/// </summary>
public class VerificationTests
{
    private readonly KernelHarness _h = new();
    private static DateTimeOffset Now => KernelHarness.Now;

    private MoundMajor Colony(bool withWitness = true)
    {
        var major = new MoundMajor(_h.Kernel, _h.Evidence);
        major.Workers.Register(new ScoutAnt(_h.Kernel, _h.Evidence));
        major.Workers.Register(new ForagerAnt(_h.Kernel, _h.Evidence));
        if (withWitness) major.Workers.Register(new WitnessAnt(new EvidenceCorrelator(_h.Evidence)));

        major.AcceptCharter(KernelHarness.NewCharter(Now), Now);
        return major;
    }

    /// <summary>act, then look again and say whether it worked.</summary>
    private static Mission Watering(DateTimeOffset now) => new()
    {
        MissionId = "ms-verify",
        MoundId = KernelHarness.MoundId,
        CharterId = "c-0001",
        ExpiresAt = now.AddMinutes(30).ToWire(),
        Steps =
        [
            new MissionStep
            {
                StepId = "water", Op = MissionStepOps.Act, Capability = KernelHarness.Relay,
                Parameters = { ["on_s"] = 5 }
            },
            new MissionStep
            {
                StepId = "confirm", Op = MissionStepOps.Verify,
                Capability = KernelHarness.Sensor, Confirms = "water"
            }
        ]
    };

    private ActionRecord Watered(MoundMajor major) =>
        major.Actions.Single(a => a.Capability == KernelHarness.Relay);

    [Fact]
    public void A_confirmed_action_keeps_its_outcome_and_gains_the_proof()
    {
        _h.SensorExecutor.Reading = 42;
        var major = Colony();

        var report = major.Execute(Watering(Now), Now);
        var watered = Watered(major);

        Assert.Equal(ActionOutcomes.Succeeded, watered.Outcome);
        Assert.Equal(MissionStates.Completed, report.State);

        // The confirming reading is now part of the action's own refs, so a controller re-running
        // the gate over the synced record reaches the same verdict this did.
        Assert.Contains(watered.EvidenceRefs, r => r.StartsWith("ev-" + KernelHarness.Sensor));
    }

    /// <remarks>
    /// The sentence ARCHITECTURE.md has always carried and no code enforced: "without it the
    /// outcome is `unverified` no matter what the driver returned."
    /// </remarks>
    [Fact]
    public void An_action_whose_confirmation_produced_nothing_degrades_to_unverified()
    {
        _h.SensorExecutor.ProducesEvidence = false;   // the mound looks, and sees nothing
        var major = Colony();

        var report = major.Execute(Watering(Now), Now);

        Assert.Equal(ActionOutcomes.Unverified, Watered(major).Outcome);
        Assert.Contains("no confirming observation", Watered(major).Detail);
        Assert.Equal(MissionStates.Unverified, report.State);
    }

    /// <remarks>
    /// The step ran and ran correctly. What changed is what the mound may CLAIM about its effect,
    /// so the mission is what degrades — not the step, which would misattribute the problem to
    /// the actuation.
    /// </remarks>
    [Fact]
    public void Verification_degrades_the_mission_not_the_step()
    {
        _h.SensorExecutor.ProducesEvidence = false;
        var major = Colony();

        var report = major.Execute(Watering(Now), Now);

        Assert.Equal(MissionStepStates.Executed, report.Steps.Single(s => s.StepId == "water").State);
        Assert.Equal(MissionStates.Unverified, report.State);
    }

    /// <remarks>
    /// A refusal is a definite result, not a claim about the physical world. Demanding proof of an
    /// action that never happened would invent a failure out of a correctly reported no.
    /// </remarks>
    [Fact]
    public void A_refused_action_needs_no_confirmation()
    {
        _h.SensorExecutor.ProducesEvidence = false;
        var major = Colony();

        // Spend the relay's duty cycle so the mission's own act step is refused.
        _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now, _h.Evidence);

        major.Execute(Watering(Now), Now);

        Assert.Equal(ActionOutcomes.Refused, major.Actions.Last(a => a.Capability == KernelHarness.Relay).Outcome);
    }

    /// <remarks>
    /// Not a rule this implementation applies — a property of the gate. It returns the record's
    /// own outcome unless that outcome asserts physical work, so nothing can talk an `unverified`
    /// action back into having succeeded. A later reading proves the world's state later; it does
    /// not prove the command caused it.
    /// </remarks>
    [Fact]
    public void A_confirmation_cannot_upgrade_an_action_that_was_already_unverified()
    {
        _h.RelayExecutor.ProducesEvidence = false;   // the work happened, nothing watched it
        _h.SensorExecutor.Reading = 42;              // and then a perfectly good reading arrives
        var major = Colony();

        major.Execute(Watering(Now), Now);

        Assert.Equal(ActionOutcomes.Unverified, Watered(major).Outcome);
    }

    [Fact]
    public void With_no_witness_registered_a_verify_step_behaves_as_it_did_before()
    {
        _h.SensorExecutor.ProducesEvidence = false;
        var major = Colony(withWitness: false);

        major.Execute(Watering(Now), Now);

        Assert.Equal(ActionOutcomes.Succeeded, Watered(major).Outcome);
    }

    [Fact]
    public void The_verify_step_reports_what_it_concluded()
    {
        _h.SensorExecutor.Reading = 42;
        var major = Colony();

        var report = major.Execute(Watering(Now), Now);

        Assert.Contains("confirms 'water'", report.Steps.Single(s => s.StepId == "confirm").Detail);
    }
}
