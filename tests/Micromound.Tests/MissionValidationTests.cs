using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// <see cref="MissionValidator"/> — PROTOCOL.md §9.
///
/// A mission carries no authority of its own; it executes under a charter. So every test here is
/// really one question asked in different places: can a work packet reach the kernel holding
/// something the charter never granted? The answer has to be no *before* any step runs, because
/// ARCHITECTURE.md is explicit that a mission referencing anything outside its charter is refused
/// whole — a half-executed mission leaves physical state nobody planned, and there is no
/// compensating action for a valve that opened.
///
/// Validation is pure: registries and clocks are passed in, never read. That is what lets these
/// tests run with no hardware, no simulator, and no wall clock.
/// </summary>
public class MissionValidationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    /// <summary>The charter every mission below executes under: two capabilities, one routine.</summary>
    private static Charter ActiveCharter() => new()
    {
        CharterId = "c1",
        MoundId = "mm-1",
        MissionRef = "ms1",
        IssuedAt = Now.AddMinutes(-1).ToWire(),
        ExpiresAt = Now.AddHours(1).ToWire(),
        LeaseTtlSeconds = 900,
        ActionCeiling = "benign",
        Capabilities = ["sense.soil_moisture", "act.water_valve"],
        Routines = ["routine.water_cycle"],
        SafeState = "all_actuators_off"
    };

    /// <summary>
    /// The worked example from ARCHITECTURE.md "Structured work", as a packet: sense, water only
    /// if dry, sense again, verify, report. Deliberately the documented mission rather than a
    /// minimal one — if the doc's own example does not validate, the doc is wrong.
    /// </summary>
    private static Mission ValidMission() => new()
    {
        MissionId = "ms1",
        MoundId = "mm-1",
        CharterId = "c1",
        Worker = "Watering Ant",
        RequiredCapabilities = ["sense.soil_moisture"],
        AllowedRoutines = ["routine.water_cycle"],
        RequiredEvidence = ["soil_before", "watering_action", "soil_after"],
        SafeState = "all_actuators_off",
        ExpiresAt = Now.AddMinutes(30).ToWire(),
        Steps =
        [
            new MissionStep
            {
                StepId = "soil_before", Op = MissionStepOps.Sense,
                Capability = "sense.soil_moisture", EvidenceTag = "soil_before"
            },
            new MissionStep
            {
                StepId = "water", Op = MissionStepOps.Routine,
                RoutineId = "routine.water_cycle", EvidenceTag = "watering_action",
                Condition = new StepCondition
                {
                    SourceStep = "soil_before", Op = ConditionOps.LessThan, Value = 20
                }
            },
            new MissionStep
            {
                StepId = "soil_after", Op = MissionStepOps.Sense,
                Capability = "sense.soil_moisture", EvidenceTag = "soil_after"
            },
            new MissionStep
            {
                StepId = "confirm", Op = MissionStepOps.Verify, Capability = "sense.soil_moisture"
            },
            new MissionStep { StepId = "report", Op = MissionStepOps.Report }
        ]
    };

    private static ValidationResult Validate(Mission mission) =>
        MissionValidator.Validate(mission, ActiveCharter(), "mm-1", Now);

    [Fact]
    public void The_documented_mission_validates()
    {
        var result = Validate(ValidMission());
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void A_mission_addressed_to_another_mound_is_refused()
    {
        var mission = ValidMission();
        mission.MoundId = "mm-OTHER";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("mound_id mismatch"));
    }

    /// <remarks>
    /// A mission naming a charter that is not the active one is not merely stale — it is a claim
    /// about authority the mound cannot check. Refusing is the only fail-closed answer.
    /// </remarks>
    [Fact]
    public void A_mission_citing_a_charter_that_is_not_active_is_refused()
    {
        var mission = ValidMission();
        mission.CharterId = "c-superseded";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("c-superseded"));
    }

    [Fact]
    public void An_expired_mission_is_refused()
    {
        var mission = ValidMission();
        mission.ExpiresAt = Now.AddSeconds(-1).ToWire();

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("already expired"));
    }

    [Fact]
    public void A_mission_with_no_steps_is_refused()
    {
        var mission = ValidMission();
        mission.Steps.Clear();
        mission.RequiredEvidence.Clear();

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no steps"));
    }

    [Fact]
    public void Duplicate_step_ids_are_refused()
    {
        var mission = ValidMission();
        mission.Steps[2].StepId = "soil_before";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("duplicate step_id"));
    }

    /// <remarks>
    /// The validator stops inspecting a step whose op it does not recognise, and that is correct:
    /// every later check is op-specific, so continuing would report consequences of a step nobody
    /// can execute. This test pins the `continue` by giving the bad step an ungranted capability
    /// as well — the authority error must NOT appear, because the step is already refused.
    /// </remarks>
    [Fact]
    public void An_unknown_op_refuses_the_step_without_inspecting_it_further()
    {
        var mission = ValidMission();
        mission.Steps[0].Op = "measure";
        mission.Steps[0].Capability = "act.plasma_cutter";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("unknown op 'measure'", result.Errors[0]);
        Assert.DoesNotContain(result.Errors, e => e.Contains("plasma_cutter"));
    }

    [Fact]
    public void A_sense_step_with_no_capability_is_refused()
    {
        var mission = ValidMission();
        mission.Steps[0].Capability = "";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("requires a capability"));
    }

    [Fact]
    public void A_capability_the_charter_never_granted_is_refused()
    {
        var mission = ValidMission();
        mission.Steps[0].Capability = "sense.reactor_core_temp";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("sense.reactor_core_temp") && e.Contains("not granted"));
    }

    [Fact]
    public void An_act_step_is_held_to_the_charter_like_any_other()
    {
        var mission = ValidMission();
        mission.Steps[1] = new MissionStep
        {
            StepId = "cut", Op = MissionStepOps.Act,
            Capability = "act.plasma_cutter", EvidenceTag = "watering_action"
        };

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("act.plasma_cutter") && e.Contains("not granted"));
    }

    /// <remarks>
    /// Not a permission question. A `sense` step naming an actuator is a mission that means
    /// something other than what it says, and left to the kernel it would be refused later for
    /// the wrong reason — a Scout Ant's ceiling — with the actual mistake never named.
    /// </remarks>
    [Fact]
    public void A_sense_step_naming_an_actuator_is_refused()
    {
        var mission = ValidMission();
        mission.Steps[0].Capability = "act.water_valve";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("must be in the 'sense.' namespace"));
    }

    [Fact]
    public void An_act_step_naming_a_sensor_is_refused()
    {
        var mission = ValidMission();
        mission.Steps[1] = new MissionStep
        {
            StepId = "water", Op = MissionStepOps.Act,
            Capability = "sense.soil_moisture", EvidenceTag = "watering_action"
        };

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("must be in the 'act.' namespace"));
    }

    /// <remarks>
    /// Two documents naming different de-energized states is a contradiction nobody can resolve
    /// at the moment it matters, which is when the watchdog trips.
    /// </remarks>
    [Fact]
    public void A_safe_state_that_contradicts_the_charter_is_refused()
    {
        var mission = ValidMission();
        mission.SafeState = "hold_last_position";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("contradicts the charter"));
    }

    /// <summary>Empty means "inherit the charter's", which is the common case and not an error.</summary>
    [Fact]
    public void An_absent_safe_state_inherits_rather_than_conflicts()
    {
        var mission = ValidMission();
        mission.SafeState = "";

        Assert.True(Validate(mission).IsValid);
    }

    [Fact]
    public void A_routine_the_charter_never_enabled_is_refused()
    {
        var mission = ValidMission();
        mission.Steps[1].RoutineId = "routine.emergency_shutdown";
        mission.AllowedRoutines = ["routine.emergency_shutdown"];

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("routine.emergency_shutdown"));
    }

    /// <remarks>
    /// Charter-enabled but outside the mission's own `allowed_routines`. The charter is the
    /// ceiling; the mission may narrow it, and narrowing has to actually bind or it is decoration.
    /// </remarks>
    [Fact]
    public void A_routine_outside_the_missions_own_allowed_list_is_refused()
    {
        var charter = ActiveCharter();
        charter.Routines = ["routine.water_cycle", "routine.dock"];

        var mission = ValidMission();
        mission.Steps[1].RoutineId = "routine.dock";

        var result = MissionValidator.Validate(mission, charter, "mm-1", Now);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("allowed_routines"));
    }

    [Fact]
    public void A_condition_reading_a_step_that_has_not_run_yet_is_refused()
    {
        var mission = ValidMission();
        mission.Steps[1].Condition!.SourceStep = "soil_after";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does not run first"));
    }

    [Fact]
    public void A_condition_reading_its_own_step_is_refused()
    {
        var mission = ValidMission();
        mission.Steps[1].Condition!.SourceStep = "water";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does not run first"));
    }

    [Fact]
    public void A_condition_naming_no_step_at_all_is_refused()
    {
        var mission = ValidMission();
        mission.Steps[1].Condition!.SourceStep = "nowhere";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("is not a step in this mission"));
    }

    [Theory]
    [InlineData("<")]
    [InlineData("less_than")]
    [InlineData("contains")]
    [InlineData("")]
    public void Condition_operators_outside_the_closed_set_are_refused(string op)
    {
        var mission = ValidMission();
        mission.Steps[1].Condition!.Op = op;

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unknown condition op"));
    }

    /// <remarks>
    /// `confirms` is what makes a `verify` step different from a `sense` step at all — the link
    /// between a confirming observation and the action it confirms.
    /// </remarks>
    [Fact]
    public void A_verify_step_may_confirm_an_earlier_actuating_step()
    {
        var mission = ValidMission();
        mission.Steps[3].Confirms = "water";

        Assert.True(Validate(mission).IsValid, string.Join("; ", Validate(mission).Errors));
    }

    [Fact]
    public void Only_a_verify_step_may_confirm_another()
    {
        var mission = ValidMission();
        mission.Steps[2].Confirms = "water";   // a `sense` step

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("only a 'verify' step may confirm"));
    }

    [Fact]
    public void Confirming_a_step_that_has_not_run_yet_is_refused()
    {
        var mission = ValidMission();
        mission.Steps[3].Confirms = "report";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does not run first"));
    }

    /// <remarks>
    /// Confirming an observation is not confirmation of anything. The point of a verify step is to
    /// say whether physical work had an effect, and only an act or a routine claims to have had one.
    /// </remarks>
    [Fact]
    public void Confirming_a_step_that_does_not_actuate_is_refused()
    {
        var mission = ValidMission();
        mission.Steps[3].Confirms = "soil_before";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does not actuate"));
    }

    [Fact]
    public void Confirming_a_step_that_is_not_in_the_mission_is_refused()
    {
        var mission = ValidMission();
        mission.Steps[3].Confirms = "nowhere";

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("is not a step in this mission"));
    }

    [Fact]
    public void Required_capabilities_are_checked_even_when_no_step_uses_them()
    {
        var mission = ValidMission();
        mission.RequiredCapabilities = ["act.plasma_cutter"];

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("required capability") && e.Contains("act.plasma_cutter"));
    }

    /// <remarks>
    /// "Commands are not evidence" starts here, before anything runs: a mission that promises
    /// evidence no step is tagged to produce could only ever finish `unverified`. Refusing at
    /// validation says so while the physical world is still untouched.
    /// </remarks>
    [Fact]
    public void Evidence_the_mission_promises_but_no_step_produces_is_refused()
    {
        var mission = ValidMission();
        mission.RequiredEvidence.Add("flow_pulse_count");

        var result = Validate(mission);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("flow_pulse_count") && e.Contains("not produced"));
    }

    /// <summary>`report` touches no hardware, so it is the one op that needs no capability.</summary>
    [Fact]
    public void A_report_step_needs_no_capability()
    {
        var mission = ValidMission();
        mission.Steps = [mission.Steps[^1]];
        mission.RequiredCapabilities.Clear();
        mission.AllowedRoutines.Clear();
        mission.RequiredEvidence.Clear();

        var result = Validate(mission);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Rejections_carry_full_error_lists_never_just_the_first()
    {
        var result = MissionValidator.Validate(new Mission(), ActiveCharter(), "mm-1", Now);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 5, string.Join("; ", result.Errors));
    }
}
