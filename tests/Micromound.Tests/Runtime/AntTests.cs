using Micromound.Capabilities;
using Micromound.Protocol;
using Micromound.Runtime;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The three ants a mission passes through — ANTS.md, M2.
///
/// The question worth asking of each is not "does it forward the call" but "what can it not do".
/// Scout and Forager exist to stamp their own ceiling onto every request they make, so that a
/// worker's declared limit is a property of the worker rather than of whoever called it. Guard
/// exists to make the mound do less, and can do nothing else.
/// </summary>
public class AntTests
{
    private readonly KernelHarness _h = new();
    private static DateTimeOffset Now => KernelHarness.Now;

    // -------------------------------------------------------------------------------------
    // Scout and Forager: the ceiling belongs to the worker
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_forager_submits_under_its_own_ceiling_and_the_work_happens()
    {
        _h.AcceptCharter(Now);
        var forager = new ForagerAnt(_h.Kernel, _h.Evidence);

        var record = forager.Request(
            new CapabilityRequest { Capability = KernelHarness.Relay, Parameters = { ["on_s"] = 5 } }, Now);

        Assert.Equal(ActionOutcomes.Succeeded, record.Outcome);
        Assert.Equal(1, _h.RelayExecutor.Calls);
        Assert.Equal(1, forager.Requests);
    }

    /// <remarks>
    /// A Scout is declared `observe`. Asked to open a valve under a `benign` charter that would
    /// otherwise permit it, the answer must be no — and it must come from the kernel, naming the
    /// class, rather than from the ant quietly declining. One decider.
    /// </remarks>
    [Fact]
    public void A_scout_cannot_actuate_even_under_a_charter_that_would_allow_it()
    {
        _h.AcceptCharter(Now);
        var scout = new ScoutAnt(_h.Kernel, _h.Evidence);

        var record = scout.Sense(
            new CapabilityRequest { Capability = KernelHarness.Relay, Parameters = { ["on_s"] = 5 } }, Now);

        Assert.Equal(ActionOutcomes.Refused, record.Outcome);
        Assert.Contains("action_class_exceeded", record.Detail);
        Assert.Equal(0, _h.RelayExecutor.Calls);
    }

    /// <remarks>
    /// The ceiling that binds is the one belonging to the worker that made the request, never one
    /// supplied by the caller. Otherwise a worker's declared limit would be advice.
    /// </remarks>
    [Fact]
    public void An_ant_cannot_be_handed_a_ceiling_that_is_not_its_own()
    {
        _h.AcceptCharter(Now);
        var scout = new ScoutAnt(_h.Kernel, _h.Evidence);

        var record = scout.Sense(new CapabilityRequest
        {
            Capability = KernelHarness.Relay,
            WorkerCeiling = ActionClass.Controlled,   // ignored: not the Scout's
            Parameters = { ["on_s"] = 5 }
        }, Now);

        Assert.Equal(ActionOutcomes.Refused, record.Outcome);
        Assert.Equal(0, _h.RelayExecutor.Calls);
    }

    [Fact]
    public void A_scouts_reading_comes_back_as_evidence_not_a_bare_number()
    {
        _h.AcceptCharter(Now);
        _h.SensorExecutor.Reading = 17;
        var scout = new ScoutAnt(_h.Kernel, _h.Evidence);

        var record = scout.Sense(new CapabilityRequest { Capability = KernelHarness.Sensor }, Now);

        Assert.Equal(ActionOutcomes.Succeeded, record.Outcome);
        Assert.NotEmpty(record.EvidenceRefs);
        Assert.True(_h.Evidence.TryGet(record.EvidenceRefs[0], out var item));
        Assert.True(EvidenceReadings.TryRead(item, out var value));
        Assert.Equal(17, value);
    }

    // -------------------------------------------------------------------------------------
    // Guard: the software watchdog SAFETY.md Layer 1 has always promised
    // -------------------------------------------------------------------------------------

    [Fact]
    public void A_guard_that_has_never_been_beaten_demands_a_safe_state()
    {
        var guard = new GuardAnt(heartbeatTimeoutSeconds: 30);

        guard.Poll(Now);

        Assert.True(guard.SafeStateRequired);
        Assert.Contains("heartbeat stale", guard.Reason);
    }

    [Fact]
    public void A_fresh_heartbeat_satisfies_the_watchdog()
    {
        var guard = new GuardAnt(heartbeatTimeoutSeconds: 30);
        guard.Beat(Now);

        guard.Poll(Now.AddSeconds(5));

        Assert.False(guard.SafeStateRequired);
        Assert.Equal("", guard.Reason);
    }

    /// <remarks>
    /// A stale heartbeat is self-healing on purpose. A watchdog that latched on a scheduling
    /// hiccup is a watchdog nobody leaves enabled, and a disabled watchdog protects nothing.
    /// </remarks>
    [Fact]
    public void A_heartbeat_that_resumes_clears_the_demand()
    {
        var guard = new GuardAnt(heartbeatTimeoutSeconds: 30);
        guard.Beat(Now);
        guard.Poll(Now.AddSeconds(60));
        Assert.True(guard.SafeStateRequired);

        guard.Beat(Now.AddSeconds(61));
        guard.Poll(Now.AddSeconds(62));

        Assert.False(guard.SafeStateRequired);
    }

    /// <remarks>
    /// SAFETY.md Layer 0: a Guard Ant reports an interlock trip, it does not clear one. There is
    /// no method on this class that clears a trip — this pins the consequence, that a healthy
    /// heartbeat afterwards does not talk the mound back into acting.
    /// </remarks>
    [Fact]
    public void A_reported_trip_survives_a_healthy_heartbeat()
    {
        var guard = new GuardAnt(heartbeatTimeoutSeconds: 30);
        guard.Beat(Now);
        guard.ReportTrip("thermal_cutout", "compressor head over temperature");

        guard.Beat(Now.AddSeconds(1));
        guard.Poll(Now.AddSeconds(1));

        Assert.True(guard.SafeStateRequired);
        Assert.Contains("thermal_cutout", guard.Reason);
    }

    [Fact]
    public void A_zero_timeout_disables_the_heartbeat_check_explicitly()
    {
        var guard = new GuardAnt(heartbeatTimeoutSeconds: 0);

        guard.Poll(Now);

        Assert.False(guard.SafeStateRequired);
    }

    /// <remarks>
    /// A mound that entered its safe state has to be able to prove afterwards why it did.
    /// "It just stopped" is the silent kind of failure SAFETY.md forbids.
    /// </remarks>
    [Fact]
    public void Guard_health_is_reported_as_readable_evidence()
    {
        var guard = new GuardAnt(heartbeatTimeoutSeconds: 30);
        guard.Beat(Now);

        var items = guard.Poll(Now.AddSeconds(7));

        Assert.Single(items);
        Assert.True(EvidenceReadings.TryRead(items[0], out var ageSeconds));
        Assert.Equal(7, ageSeconds);
    }

    [Fact]
    public void Two_polls_in_the_same_second_do_not_collide()
    {
        var guard = new GuardAnt(heartbeatTimeoutSeconds: 30);

        var first = guard.Poll(Now);
        var second = guard.Poll(Now);

        Assert.NotEqual(first[0].EvidenceId, second[0].EvidenceId);
    }

    // -------------------------------------------------------------------------------------
    // The coordinator dispatching through them
    // -------------------------------------------------------------------------------------

    private MoundMajor Colony(out ScoutAnt scout, out ForagerAnt forager, GuardAnt? guard = null)
    {
        var major = new MoundMajor(_h.Kernel, _h.Evidence);
        scout = new ScoutAnt(_h.Kernel, _h.Evidence);
        forager = new ForagerAnt(_h.Kernel, _h.Evidence);

        major.Workers.Register(scout);
        major.Workers.Register(forager);
        if (guard is not null) major.Workers.Register(guard);

        major.AcceptCharter(KernelHarness.NewCharter(Now), Now);
        return major;
    }

    /// <summary>sense, act, sense, report — the shortest mission that has something to lose.</summary>
    private static Mission Watering(DateTimeOffset now) => new()
    {
        MissionId = "ms-ant",
        MoundId = KernelHarness.MoundId,
        CharterId = "c-0001",
        ExpiresAt = now.AddMinutes(30).ToWire(),
        Steps =
        [
            new MissionStep { StepId = "before", Op = MissionStepOps.Sense, Capability = KernelHarness.Sensor },
            new MissionStep
            {
                StepId = "water", Op = MissionStepOps.Act, Capability = KernelHarness.Relay,
                Parameters = { ["on_s"] = 5 }
            },
            new MissionStep { StepId = "after", Op = MissionStepOps.Sense, Capability = KernelHarness.Sensor },
            new MissionStep { StepId = "report", Op = MissionStepOps.Report }
        ]
    };

    [Fact]
    public void The_scout_runs_the_sensing_and_the_forager_runs_the_action()
    {
        _h.SensorExecutor.Reading = 17;
        var major = Colony(out var scout, out var forager);

        var report = major.Execute(Watering(Now), Now);

        Assert.Equal(MissionStates.Completed, report.State);
        Assert.Equal(2, scout.Requests);
        Assert.Equal(1, forager.Requests);
    }

    /// <remarks>
    /// An application ant declared in a manifest with no code behind it yet must not be silently
    /// replaced by a default ant — substituting one would apply a ceiling the mission never asked
    /// for, in whichever direction happened to be convenient.
    /// </remarks>
    [Fact]
    public void A_named_worker_that_is_not_an_ant_is_not_quietly_substituted()
    {
        _h.SensorExecutor.Reading = 17;
        var major = Colony(out _, out var forager);
        major.Workers.Register(new FakeWorker("Watering Ant", ActionClass.Observe));

        var mission = Watering(Now);
        mission.Worker = "Watering Ant";

        var report = major.Execute(mission, Now);

        Assert.Equal(MissionStepStates.Refused, report.Steps.Single(s => s.StepId == "water").State);
        Assert.Equal(0, forager.Requests);
        Assert.Equal(0, _h.RelayExecutor.Calls);
    }

    /// <remarks>
    /// SAFETY.md Layer 1, made real: loss of the runtime's own heartbeat drops actuation and
    /// enters the declared safe state. Engaging the stop is deliberate — recovery then runs
    /// through the one path that restores nothing.
    /// </remarks>
    [Fact]
    public void A_guard_demanding_a_safe_state_stops_the_actuation_and_the_mound()
    {
        _h.SensorExecutor.Reading = 17;
        var major = Colony(out _, out var forager, new GuardAnt(heartbeatTimeoutSeconds: 30));

        var report = major.Execute(Watering(Now), Now);   // no heartbeat was ever sent

        var water = report.Steps.Single(s => s.StepId == "water");
        Assert.Equal(MissionStepStates.Stopped, water.State);
        Assert.Contains("heartbeat stale", water.Detail);
        Assert.Equal(0, forager.Requests);
        Assert.Equal(0, _h.RelayExecutor.Calls);
        Assert.Equal(MissionStates.Stopped, report.State);
        Assert.Equal(MoundStates.Stopped, major.State);
    }

    /// <remarks>
    /// The payoff, and the reason the kernel's stop check had to change: PROTOCOL.md §7 says a
    /// stopped mound keeps sensing, and §7 also wants the stop acknowledgement to carry a
    /// post-stop sensor snapshot. A mound that downed tools entirely could never take one.
    /// </remarks>
    [Fact]
    public void After_a_guard_trip_the_mound_stops_acting_but_keeps_looking()
    {
        _h.SensorExecutor.Reading = 17;
        var major = Colony(out var scout, out _, new GuardAnt(heartbeatTimeoutSeconds: 30));

        var report = major.Execute(Watering(Now), Now);

        Assert.Equal(MissionStepStates.Executed, report.Steps.Single(s => s.StepId == "after").State);
        Assert.Equal(2, scout.Requests);
        Assert.Equal(2, _h.SensorExecutor.Calls);
    }

    [Fact]
    public void A_beaten_guard_lets_the_mission_run()
    {
        _h.SensorExecutor.Reading = 17;
        var guard = new GuardAnt(heartbeatTimeoutSeconds: 30);
        guard.Beat(Now);
        var major = Colony(out _, out var forager, guard);

        var report = major.Execute(Watering(Now), Now);

        Assert.Equal(MissionStates.Completed, report.State);
        Assert.Equal(1, forager.Requests);
        Assert.Equal(MoundStates.Chartered, major.State);
    }
}
