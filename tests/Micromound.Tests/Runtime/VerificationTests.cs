using Micromound.Capabilities;
using Micromound.Evidence;
using Micromound.Protocol;
using Micromound.Runtime;
using Micromound.Sync;
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
    /// Unacknowledged proof is retained past the soft capacity — never silently dropped — but not
    /// without bound. Past the hard ceiling the oldest unacknowledged item spills, and the spill is
    /// counted, not silent: the deliberate answer to "a mound offline for a week".
    /// </remarks>
    [Fact]
    public void Unacknowledged_proof_spills_oldest_first_past_the_hard_ceiling()
    {
        var store = new InMemoryEvidenceStore(capacity: 2, hardCeiling: 3);
        store.Add(Item("e1"));
        store.Add(Item("e2"));
        store.Add(Item("e3"));

        Assert.Equal(3, store.Count);           // at the ceiling, nothing spilled yet
        Assert.Equal(0, store.TakeSpilledCount());

        store.Add(Item("e4"));                  // past the ceiling: the oldest unacked spills

        Assert.Equal(3, store.Count);
        Assert.False(store.TryGet("e1", out _));   // the oldest went
        Assert.True(store.TryGet("e4", out _));    // the newest stayed
        Assert.Equal(1, store.TakeSpilledCount()); // and it was a spill, not an eviction
        Assert.Equal(0, store.TakeEvictedCount());
    }

    /// <remarks>Acknowledged proof — already delivered — is always reclaimed before any unacknowledged spill.</remarks>
    [Fact]
    public void Acknowledged_proof_is_reclaimed_before_unacknowledged_spills()
    {
        var store = new InMemoryEvidenceStore(capacity: 2, hardCeiling: 3);
        store.Add(Item("e1"));
        store.Add(Item("e2"));
        store.Add(Item("e3"));
        store.Acknowledge(["e1"]);   // the oldest is now delivered

        store.Add(Item("e4"));       // pressure: reclaim the acked e1, do not spill anything

        Assert.False(store.TryGet("e1", out _));
        Assert.Equal(1, store.TakeEvictedCount());
        Assert.Equal(0, store.TakeSpilledCount());
        Assert.True(store.TryGet("e2", out _) && store.TryGet("e4", out _));
    }

    /// <summary>Like the evicted count, a spill rides the next bundle exactly once.</summary>
    [Fact]
    public void The_spill_count_is_reported_once()
    {
        var store = new InMemoryEvidenceStore(capacity: 1, hardCeiling: 1);
        store.Add(Item("e1"));
        store.Add(Item("e2"));   // e1 spills

        Assert.Equal(1, store.TakeSpilledCount());
        Assert.Equal(0, store.TakeSpilledCount());
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

    /// <remarks>
    /// The intent → execute → result checkpoint discipline the recovery path depends on: an
    /// actuation is marked in flight BEFORE it reaches hardware and cleared AFTER its record is in
    /// hand. A store that remembers every write proves both halves happened — without this, dropping
    /// either write in a refactor leaves every recovery test that plants a checkpoint by hand green
    /// while the real ambiguous window is no longer recorded.
    /// </remarks>
    [Fact]
    public void A_running_actuation_is_marked_in_flight_then_cleared()
    {
        _h.SensorExecutor.Reading = 42;
        var store = new RecordingStore();

        var major = new MoundMajor(_h.Kernel, _h.Evidence);
        major.Workers.Register(new ScoutAnt(_h.Kernel, _h.Evidence));
        major.Workers.Register(new ForagerAnt(_h.Kernel, _h.Evidence));
        major.Workers.Register(new WitnessAnt(new EvidenceCorrelator(_h.Evidence)));
        major.Workers.Register(new CacheAnt(store));
        major.AcceptCharter(KernelHarness.NewCharter(Now), Now);

        major.Execute(Watering(Now), Now);

        var missionWrites = store.Writes
            .Where(w => w.Key == "cache:" + MissionCheckpoint.Key).Select(w => w.Value).ToList();

        Assert.Contains(missionWrites, v => v.Contains("\"actuation_in_flight\":\"water\""));   // intent persisted
        Assert.DoesNotContain("\"actuation_in_flight\":\"water\"", missionWrites[^1]);           // result persisted: in-flight cleared after the record

        // Execute no longer deletes the checkpoint — the checkpoint (intent) is cleared only after
        // the terminal report (result) is durably queued, by whoever publishes it. So after a raw
        // Execute the checkpoint still stands; ClearMissionCheckpoint is the explicit clear.
        Assert.True(store.TryGet("cache:" + MissionCheckpoint.Key, out _));
        major.ClearMissionCheckpoint();
        Assert.False(store.TryGet("cache:" + MissionCheckpoint.Key, out _));
    }

    /// <summary>An <see cref="IStateStore"/> that remembers every write, so a write a later delete hides is still provable.</summary>
    private sealed class RecordingStore : IStateStore
    {
        private readonly InMemoryStateStore _inner = new();
        public List<(string Key, string Value)> Writes { get; } = [];

        public void Put(string key, string value) { Writes.Add((key, value)); _inner.Put(key, value); }
        public bool TryGet(string key, out string value) => _inner.TryGet(key, out value);
        public void Delete(string key) => _inner.Delete(key);
    }
}

/// <summary>
/// The temporal half of "commands are not evidence": a confirming reading from BEFORE the action
/// began cannot be evidence of the action's effect. Until now the gate checked that a confirming
/// reading was fresh and not from the future, but not that it followed the act — so a stale reading
/// with the right tag, or one reordered by clock skew, could confirm an effect that had not yet
/// happened.
/// </summary>
public class WitnessOrderingTests
{
    private static DateTimeOffset Now => KernelHarness.Now;

    private static WitnessAnt Witness() => new(new EvidenceCorrelator(new InMemoryEvidenceStore()));

    private static ActionRecord Watered(DateTimeOffset began) => new()
    {
        ActionId = "a1",
        Capability = "act.relay_1",
        Outcome = ActionOutcomes.Succeeded,
        StartedAt = began.ToWire(),
        EndedAt = began.AddSeconds(5).ToWire()
    };

    private static EvidenceItem Reading(string id, DateTimeOffset capturedAt) =>
        EvidenceReadings.Create(id, "sense.temp", 26, capturedAt, source: "fake.sense.temp");

    [Fact]
    public void A_reading_from_before_the_act_cannot_confirm_it()
    {
        var record = Watered(Now);

        var outcome = Witness().Confirm(record, [Reading("ev-before", Now.AddSeconds(-30))],
            new EvidencePolicy(), Now.AddSeconds(6), out var reason);

        Assert.Equal(ActionOutcomes.Unverified, outcome);
        Assert.Contains("predates the action", reason);
        // A pre-act reading is no more part of the proof than the command is.
        Assert.DoesNotContain("ev-before", record.EvidenceRefs);
    }

    [Fact]
    public void A_reading_taken_after_the_act_confirms_it()
    {
        var record = Watered(Now);

        var outcome = Witness().Confirm(record, [Reading("ev-after", Now.AddSeconds(2))],
            new EvidencePolicy(), Now.AddSeconds(6), out _);

        Assert.Equal(ActionOutcomes.Succeeded, outcome);
        Assert.Contains("ev-after", record.EvidenceRefs);
    }

    [Fact]
    public void A_reading_at_the_instant_the_act_began_still_confirms()
    {
        // The boundary the synchronous runtime actually produces: the mission walks at one clock,
        // so the confirming reading is stamped the same second the action started. That must count.
        var record = Watered(Now);

        var outcome = Witness().Confirm(record, [Reading("ev-at", Now)],
            new EvidencePolicy(), Now.AddSeconds(6), out _);

        Assert.Equal(ActionOutcomes.Succeeded, outcome);
    }

    [Fact]
    public void Among_mixed_readings_only_the_ones_after_the_act_become_proof()
    {
        var record = Watered(Now);

        var outcome = Witness().Confirm(record,
            [Reading("ev-before", Now.AddSeconds(-30)), Reading("ev-after", Now.AddSeconds(2))],
            new EvidencePolicy(), Now.AddSeconds(6), out _);

        Assert.Equal(ActionOutcomes.Succeeded, outcome);
        Assert.Contains("ev-after", record.EvidenceRefs);
        Assert.DoesNotContain("ev-before", record.EvidenceRefs);
    }

    [Fact]
    public void An_action_with_no_parseable_time_imposes_no_ordering()
    {
        // Ordering it cannot justify would be worse than none: with no action clock, fall back to
        // the gate's own freshness rules rather than inventing a "before".
        var record = new ActionRecord
        {
            ActionId = "a1", Capability = "act.relay_1", Outcome = ActionOutcomes.Succeeded
        };

        var outcome = Witness().Confirm(record, [Reading("ev-x", Now.AddSeconds(-30))],
            new EvidencePolicy(), Now, out _);

        Assert.Equal(ActionOutcomes.Succeeded, outcome);
    }
}
