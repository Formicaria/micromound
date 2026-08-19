using Micromound.Capabilities;
using Micromound.Protocol;
using Micromound.Runtime;
using Xunit;

namespace Micromound.Tests;

/// <summary>A worker with a declared ceiling and nothing else — enough to be registered.</summary>
internal sealed class FakeWorker(string name, ActionClass ceiling) : IMoundWorker
{
    public WorkerDescriptor Descriptor { get; } = new() { Name = name, Ceiling = ceiling };

    public WorkerState State => WorkerState.Idle;
}

/// <summary>
/// <see cref="MoundMajor"/> — the M1 deliverable, and the first thing in this repository that
/// executes a mission rather than merely validating one.
///
/// The passing case is the worked example from ARCHITECTURE.md "Structured work", as a packet:
/// sense soil, water only if dry, sense again, report. Every other test here bends one thing
/// about it, because the interesting question is never "does the happy path work" but "what does
/// this thing do when something goes wrong halfway through, with a valve already open".
///
/// Nothing here asserts that the coordinator refuses anything on its own. It cannot: it holds no
/// executor and every actuation goes through the kernel. What these tests hold it to is ORDER
/// and EVIDENCE — what runs, what stops running, and what the mound is entitled to claim
/// afterwards.
/// </summary>
public class MissionTests
{
    private static DateTimeOffset Now => KernelHarness.Now;

    private readonly KernelHarness _h = new();
    private readonly MoundMajor _major;

    public MissionTests()
    {
        _major = new MoundMajor(_h.Kernel, _h.Evidence);
        _h.SensorExecutor.Reading = 17;      // dry soil: the documented mission waters
    }

    /// <summary>ARCHITECTURE.md "Structured work", as an executable packet.</summary>
    private static Mission Watering(DateTimeOffset now, Action<Mission>? adjust = null)
    {
        var mission = new Mission
        {
            MissionId = "ms-0001",
            MoundId = KernelHarness.MoundId,
            CharterId = "c-0001",
            RequiredCapabilities = [KernelHarness.Sensor],
            AllowedRoutines = [KernelHarness.WaterCycle],
            RequiredEvidence = ["soil_before", "watering_action", "soil_after"],
            SafeState = "all_actuators_off",
            ExpiresAt = now.AddMinutes(30).ToWire(),
            Steps =
            [
                new MissionStep
                {
                    StepId = "soil_before", Op = MissionStepOps.Sense,
                    Capability = KernelHarness.Sensor, EvidenceTag = "soil_before"
                },
                new MissionStep
                {
                    StepId = "water", Op = MissionStepOps.Routine,
                    RoutineId = KernelHarness.WaterCycle, EvidenceTag = "watering_action",
                    Parameters = { ["on_s"] = 10 },
                    Condition = new StepCondition
                    {
                        SourceStep = "soil_before", Op = ConditionOps.LessThan, Value = 20
                    }
                },
                new MissionStep
                {
                    StepId = "soil_after", Op = MissionStepOps.Sense,
                    Capability = KernelHarness.Sensor, EvidenceTag = "soil_after"
                },
                new MissionStep { StepId = "report", Op = MissionStepOps.Report }
            ]
        };

        adjust?.Invoke(mission);
        return mission;
    }

    private MissionReport Run(Action<Mission>? adjust = null, DateTimeOffset? at = null)
    {
        var now = at ?? Now;
        _major.AcceptCharter(KernelHarness.NewCharter(Now), Now);
        return _major.Execute(Watering(now, adjust), now);
    }

    private MissionStepResult Step(MissionReport report, string stepId) =>
        report.Steps.Single(s => s.StepId == stepId);

    // -------------------------------------------------------------------------------------
    // The path that works
    // -------------------------------------------------------------------------------------

    [Fact]
    public void The_documented_mission_runs_end_to_end()
    {
        var report = Run();

        Assert.Equal(MissionStates.Completed, report.State);
        Assert.Equal(4, report.Steps.Count);
        Assert.All(report.Steps, s => Assert.Equal(MissionStepStates.Executed, s.State));
        Assert.Equal(1, _h.RoutineExecutor.Calls);
    }

    [Fact]
    public void A_sensed_reading_reaches_the_step_result()
    {
        var report = Run();

        Assert.Equal(17, Step(report, "soil_before").Value);
        Assert.NotEmpty(Step(report, "soil_before").EvidenceRefs);
    }

    [Fact]
    public void A_report_step_needs_no_capability_and_no_hardware()
    {
        var report = Run();

        Assert.Equal(MissionStepStates.Executed, Step(report, "report").State);
        Assert.Empty(Step(report, "report").EvidenceRefs);
    }

    [Fact]
    public void Every_action_is_recorded_in_order()
    {
        Run();

        Assert.Equal(3, _major.Actions.Count);   // sense, routine, sense — report touches nothing
        Assert.Equal(KernelHarness.Sensor, _major.Actions[0].Capability);
        Assert.Equal(KernelHarness.WaterCycle, _major.Actions[1].Capability);
        Assert.All(_major.Actions, a => Assert.Equal("ms-0001", a.MissionId));
    }

    // -------------------------------------------------------------------------------------
    // Conditions
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// The whole point of the conditional: wet soil means the valve is never opened. Asserting
    /// the executor's call count rather than the step's label is deliberate — a coordinator that
    /// watered and then relabelled the step would pass a weaker test.
    /// </remarks>
    [Fact]
    public void A_condition_that_does_not_hold_skips_the_step_and_touches_no_hardware()
    {
        _h.SensorExecutor.Reading = 42;        // wet

        var report = Run();

        Assert.Equal(MissionStepStates.Skipped, Step(report, "water").State);
        Assert.Equal(0, _h.RoutineExecutor.Calls);
    }

    /// <remarks>
    /// A skipped step's promised evidence was never due. Grading this mission `unverified` for
    /// failing to prove a watering it correctly declined to perform would train an operator to
    /// ignore `unverified`, which is the one outcome that must keep meaning something.
    /// </remarks>
    [Fact]
    public void A_correctly_skipped_step_does_not_make_the_mission_unverified()
    {
        _h.SensorExecutor.Reading = 42;

        var report = Run();

        Assert.Equal(MissionStates.Completed, report.State);
        Assert.DoesNotContain("watering_action", report.Detail);
    }

    /// <remarks>
    /// "I could not see" and "the threshold was not met" are different facts. Collapsing them is
    /// how a mound skips watering a dying plant and reports success.
    /// </remarks>
    [Fact]
    public void A_condition_whose_source_produced_no_reading_refuses_rather_than_skipping()
    {
        _h.SensorExecutor.Reading = null;      // opaque payload: evidence, but no number

        var report = Run();

        Assert.Equal(MissionStepStates.Refused, Step(report, "water").State);
        Assert.Contains("no readable value", Step(report, "water").Detail);
        Assert.Equal(MissionStates.Refused, report.State);
        Assert.Equal(0, _h.RoutineExecutor.Calls);
    }

    // -------------------------------------------------------------------------------------
    // Refused whole, never partially run
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_mission_that_fails_validation_runs_no_steps_at_all()
    {
        var report = Run(m => m.Steps[0].Capability = "sense.reactor_core_temp");

        Assert.Equal(MissionStates.Refused, report.State);
        Assert.Empty(report.Steps);
        Assert.Equal(0, _h.SensorExecutor.Calls);
        Assert.Contains("not granted", report.Detail);
    }

    [Fact]
    public void A_mission_with_no_active_charter_is_refused()
    {
        var report = _major.Execute(Watering(Now), Now);

        Assert.Equal(MissionStates.Refused, report.State);
        Assert.Contains("never carries its own authority", report.Detail);
        Assert.Empty(report.Steps);
    }

    [Fact]
    public void A_stop_order_in_force_runs_nothing()
    {
        _major.AcceptCharter(KernelHarness.NewCharter(Now), Now);
        _major.Stop();

        var report = _major.Execute(Watering(Now), Now);

        Assert.Equal(MissionStates.Stopped, report.State);
        Assert.Empty(report.Steps);
        Assert.Equal(0, _h.SensorExecutor.Calls);
    }

    /// <remarks>Disconnection never widens authority: an expired lease is not a slow lease.</remarks>
    [Fact]
    public void An_expired_lease_quiesces_the_mission_before_any_step()
    {
        _major.AcceptCharter(KernelHarness.NewCharter(Now), Now);
        var later = Now.AddSeconds(901);       // lease_ttl_s is 900

        var report = _major.Execute(Watering(later), later);

        Assert.Equal(MissionStates.Quiesced, report.State);
        Assert.Empty(report.Steps);
        Assert.Equal(MoundStates.Quiesced, _major.State);
    }

    // -------------------------------------------------------------------------------------
    // Halting: stop acting, keep looking
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// The rule this suite exists for. After a refusal the mission must not actuate again — its
    /// premise is gone — but the observation steps still run, because the most valuable thing
    /// after a partial actuation is a reading of where the physical world was actually left.
    /// </remarks>
    [Fact]
    public void After_a_refusal_the_mission_stops_acting_but_keeps_looking()
    {
        _major.AcceptCharter(KernelHarness.NewCharter(Now), Now);

        // Spend the duty cycle first, so the mission's own routine step is refused.
        _h.Kernel.Execute(KernelHarness.Request(KernelHarness.WaterCycle, 5), Now, _h.Evidence);
        var routineCallsBefore = _h.RoutineExecutor.Calls;

        var report = _major.Execute(Watering(Now), Now);

        Assert.Equal(MissionStepStates.Refused, Step(report, "water").State);
        Assert.Equal(routineCallsBefore, _h.RoutineExecutor.Calls);          // never re-entered
        Assert.Equal(MissionStepStates.Executed, Step(report, "soil_after").State);  // still looked
        Assert.Equal(2, _h.SensorExecutor.Calls);
        Assert.Equal(MissionStates.Refused, report.State);
    }

    /// <remarks>
    /// A hardware fault followed by suppressed steps is a mission that FAILED. Grading it
    /// `refused` because the suppression label happens to outrank `failed` would blame authority
    /// for a broken pump, and someone would go read the charter instead of the relay.
    /// </remarks>
    [Fact]
    public void A_driver_fault_is_reported_as_failed_not_refused()
    {
        _h.RoutineExecutor.Faults = true;

        var report = Run();

        Assert.Equal(MissionStepStates.Failed, Step(report, "water").State);
        Assert.Equal(MissionStates.Failed, report.State);
    }

    [Fact]
    public void A_later_actuating_step_is_refused_once_the_mission_has_halted()
    {
        _h.RoutineExecutor.Faults = true;

        var report = Run(m => m.Steps.Insert(3, new MissionStep
        {
            StepId = "top_up", Op = MissionStepOps.Act, Capability = KernelHarness.Relay,
            Parameters = { ["on_s"] = 5 }
        }));

        Assert.Equal(MissionStepStates.Refused, Step(report, "top_up").State);
        Assert.Contains("halted by an earlier step", Step(report, "top_up").Detail);
        Assert.Equal(0, _h.RelayExecutor.Calls);
        Assert.Equal(MissionStates.Failed, report.State);   // the fault, not the suppression
    }

    // -------------------------------------------------------------------------------------
    // Commands are not evidence
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// The work happened and nothing saw it. The step ran; the MISSION is what becomes
    /// unverified, because a mission is a claim about the world and this one cannot support it.
    /// </remarks>
    [Fact]
    public void An_action_nothing_observed_makes_the_mission_unverified()
    {
        _h.RoutineExecutor.ProducesEvidence = false;

        var report = Run();

        Assert.Equal(MissionStepStates.Executed, Step(report, "water").State);
        Assert.Equal(MissionStates.Unverified, report.State);
    }

    [Fact]
    public void Required_evidence_that_never_materializes_is_named_in_the_report()
    {
        _h.RoutineExecutor.ProducesEvidence = false;

        var report = Run();

        Assert.Contains("watering_action", report.Detail);
        Assert.Contains("never produced", report.Detail);
    }

    [Fact]
    public void A_clamped_action_still_counts_as_executed()
    {
        // The routine's compiled bound is 20 s; ask for 60.
        var report = Run(m => m.Steps[1].Parameters["on_s"] = 60);

        Assert.Equal(MissionStepStates.Executed, Step(report, "water").State);
        Assert.Equal(MissionStates.Completed, report.State);
        Assert.Equal(20, _h.RoutineExecutor.LastExecution!.Parameters["on_s"]);
    }

    // -------------------------------------------------------------------------------------
    // The coordinator does not decide authority
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// A worker's own ceiling from the manifest is intersected with the charter's. Registering
    /// the Forager as `observe` must stop it actuating even under a `benign` charter — and the
    /// refusal has to come from the kernel, not from a check the coordinator could forget.
    /// </remarks>
    [Fact]
    public void A_workers_own_ceiling_still_binds_under_a_permissive_charter()
    {
        _major.Workers.Register(new FakeWorker(DefaultAnts.Forager, ActionClass.Observe));

        var report = Run();

        Assert.Equal(MissionStepStates.Refused, Step(report, "water").State);
        Assert.Contains("action_class_exceeded", Step(report, "water").Detail);
        Assert.Equal(0, _h.RoutineExecutor.Calls);
    }

    [Fact]
    public void An_unregistered_worker_gets_no_ceiling_rather_than_an_invented_one()
    {
        // Nothing registered: the Forager is unknown, so the charter ceiling alone applies.
        var report = Run();

        Assert.Equal(MissionStepStates.Executed, Step(report, "water").State);
    }

    // -------------------------------------------------------------------------------------
    // Charters and manifests
    // -------------------------------------------------------------------------------------

    [Fact]
    public void An_invalid_charter_is_refused_and_leaves_the_mound_untouched()
    {
        var result = _major.AcceptCharter(
            KernelHarness.NewCharter(Now, c => c.ActionCeiling = "hazardous"), Now);

        Assert.False(result.IsValid);
        Assert.Equal(MoundStates.ObserveOnly, _major.State);
    }

    /// <remarks>
    /// A widening attempt is already inert — the kernel intersects rather than replaces — but
    /// SAFETY.md forbids silent anything, and an author who believes they granted a 600-second
    /// run on a 20-second routine has a misunderstanding that would otherwise surface in a field
    /// as an unexplained clamp.
    /// </remarks>
    [Fact]
    public void A_charter_that_tries_to_widen_a_hardware_bound_is_accepted_and_reported()
    {
        var result = _major.AcceptCharter(KernelHarness.NewCharter(Now, c =>
            c.Limits[KernelHarness.WaterCycle] = new CapabilityLimits { MaxOnSeconds = 600 }), Now);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Contains(_major.CharterNotes, n => n.Contains("try to widen the hardware bound"));
    }

    [Fact]
    public void An_invalid_manifest_leaves_the_previous_device_limits_in_force()
    {
        var good = new MoundManifest
        {
            ManifestId = "mf-1",
            MoundId = KernelHarness.MoundId,
            IssuedAt = Now.ToWire(),
            Capabilities = [KernelHarness.Sensor, KernelHarness.Relay],
            DeviceLimits = { [KernelHarness.Relay] = new CapabilityLimits { MaxOnSeconds = 20 } }
        };

        Assert.True(_major.ApplyManifest(good, Now).IsValid);

        var bad = new MoundManifest { ManifestId = "", MoundId = "mm-somebody-else" };
        Assert.False(_major.ApplyManifest(bad, Now).IsValid);

        Assert.Equal(20, _h.Authority.DeviceLimitsFor(KernelHarness.Relay)!.MaxOnSeconds);
    }
}
