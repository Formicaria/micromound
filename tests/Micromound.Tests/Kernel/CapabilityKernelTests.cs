using Micromound.Capabilities;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The authority boundary, exercised as a pure function. No hardware, no simulator, no wall
/// clock — every test states a charter, a request, and an instant, and asserts what the kernel
/// decided and whether the driver was reached at all.
/// </summary>
public class CapabilityKernelTests
{
    private readonly KernelHarness _h = new();
    private static DateTimeOffset Now => KernelHarness.Now;

    // -- stop ---------------------------------------------------------------------------------

    [Fact]
    public void Stop_precedes_everything_and_needs_no_charter()
    {
        _h.AcceptCharter(Now);
        _h.Authority.Stop();

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(ActionOutcomes.Stopped, record.Outcome);
        Assert.Contains("stopped", record.Detail);
        Assert.Equal(0, _h.RelayExecutor.Calls);
    }

    [Fact]
    public void Stop_is_checked_before_the_capability_is_even_recognised()
    {
        _h.Authority.Stop();

        var decision = _h.Kernel.Authorize(KernelHarness.Request("act.nonexistent", 5), Now);

        Assert.Equal(RefusalReason.Stopped, decision.Refusal);
    }

    [Fact]
    public void Clearing_a_stop_restores_no_authority_of_its_own()
    {
        _h.AcceptCharter(Now);
        _h.Authority.Stop();
        _h.Authority.ClearStop();

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(RefusalReason.NoCharter, decision.Refusal);
        Assert.Equal(MoundStates.ObserveOnly, _h.Authority.State);
    }

    [Fact]
    public void A_charter_cannot_be_accepted_while_stopped()
    {
        _h.Authority.Stop();

        var result = _h.AcceptCharter(Now);

        Assert.False(result.IsValid);
        Assert.Null(_h.Authority.ActiveCharter);
    }

    // -- recognition and availability ---------------------------------------------------------

    [Fact]
    public void An_unregistered_capability_is_refused_specifically()
    {
        _h.AcceptCharter(Now);

        var decision = _h.Kernel.Authorize(KernelHarness.Request("act.trebuchet", 5), Now);

        Assert.Equal(RefusalReason.UnknownCapability, decision.Refusal);
    }

    [Fact]
    public void A_malformed_capability_id_says_so_rather_than_saying_unknown()
    {
        _h.AcceptCharter(Now);

        var decision = _h.Kernel.Authorize(KernelHarness.Request("Act.Water_Valve", 5), Now);

        Assert.Equal(RefusalReason.UnknownCapability, decision.Refusal);
        Assert.Contains("well-formed", decision.Detail);
    }

    [Fact]
    public void A_faulted_driver_refuses_rather_than_being_attempted()
    {
        _h.AcceptCharter(Now);
        _h.RelayExecutor.IsAvailable = false;

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(ActionOutcomes.Refused, record.Outcome);
        Assert.Contains("capability_unavailable", record.Detail);
        Assert.Equal(0, _h.RelayExecutor.Calls);
    }

    [Fact]
    public void An_unregistered_routine_is_distinguished_from_an_unenabled_one()
    {
        _h.AcceptCharter(Now);

        var missing = _h.Kernel.Authorize(KernelHarness.Request("routine.launch", 5), Now);
        Assert.Equal(RefusalReason.RoutineNotRegistered, missing.Refusal);

        _h.AcceptCharter(Now, c => c.Routines = []);
        var disabled = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.WaterCycle, 5), Now);
        Assert.Equal(RefusalReason.RoutineNotEnabled, disabled.Refusal);
    }

    // -- authority ----------------------------------------------------------------------------

    [Fact]
    public void With_no_charter_sensing_is_permitted_and_actuation_is_not()
    {
        var sensed = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Sensor), Now);
        Assert.Equal(ActionOutcomes.Succeeded, sensed.Outcome);

        var actuated = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), Now);
        Assert.Equal(RefusalReason.NoCharter, actuated.Refusal);
        Assert.Equal(0, _h.RelayExecutor.Calls);
    }

    [Fact]
    public void An_expired_lease_refuses_actuation_but_keeps_chartered_sensing()
    {
        _h.AcceptCharter(Now);
        var afterExpiry = Now.AddSeconds(901);

        Assert.True(_h.Authority.QuiesceIfExpired(afterExpiry));

        var actuated = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), afterExpiry);
        Assert.Equal(RefusalReason.LeaseExpired, actuated.Refusal);

        var sensed = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Sensor), afterExpiry);
        Assert.True(sensed.Authorized);
    }

    [Fact]
    public void Sensing_a_capability_the_charter_never_granted_is_still_refused_while_quiesced()
    {
        _h.AcceptCharter(Now, c => c.Capabilities = [KernelHarness.Relay]);
        var afterExpiry = Now.AddSeconds(901);
        _h.Authority.QuiesceIfExpired(afterExpiry);

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Sensor), afterExpiry);

        Assert.Equal(RefusalReason.NotGranted, decision.Refusal);
    }

    [Fact]
    public void Only_a_fresh_charter_lifts_a_quiesce()
    {
        _h.AcceptCharter(Now);
        var afterExpiry = Now.AddSeconds(901);
        _h.Authority.QuiesceIfExpired(afterExpiry);

        // Renewal is not resumption: a quiesced mound stays quiesced until re-chartered.
        _h.Authority.RenewLease(afterExpiry);
        Assert.Equal(MoundStates.Quiesced, _h.Authority.State);

        _h.AcceptCharter(afterExpiry);
        Assert.Equal(MoundStates.Chartered, _h.Authority.State);
        Assert.True(_h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), afterExpiry).Authorized);
    }

    [Fact]
    public void A_capability_outside_the_charter_is_refused_even_though_the_device_has_it()
    {
        _h.AcceptCharter(Now, c => c.Capabilities = [KernelHarness.Sensor]);

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(RefusalReason.NotGranted, decision.Refusal);
    }

    [Fact]
    public void An_observe_ceiling_refuses_actuation_it_otherwise_granted()
    {
        _h.AcceptCharter(Now, c => c.ActionCeiling = "observe");

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(RefusalReason.ActionClassExceeded, decision.Refusal);
        Assert.Contains("observe", decision.Detail);
    }

    [Fact]
    public void A_workers_own_ceiling_binds_it_below_the_charter()
    {
        _h.AcceptCharter(Now);

        var decision = _h.Kernel.Authorize(
            KernelHarness.Request(KernelHarness.Relay, 5, ActionClass.Observe, "Scout Ant"), Now);

        Assert.Equal(RefusalReason.ActionClassExceeded, decision.Refusal);
        Assert.Contains("Scout Ant", decision.Detail);
    }

    // -- parameters ---------------------------------------------------------------------------

    [Fact]
    public void An_unknown_parameter_is_refused_rather_than_dropped()
    {
        _h.AcceptCharter(Now);
        var request = KernelHarness.Request(KernelHarness.Relay, 5);
        request.Parameters["on_sec"] = 5; // a plausible misspelling

        var decision = _h.Kernel.Authorize(request, Now);

        Assert.Equal(RefusalReason.UnknownParameter, decision.Refusal);
    }

    [Fact]
    public void A_missing_required_parameter_is_refused_not_defaulted()
    {
        _h.AcceptCharter(Now);

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay), Now);

        Assert.Equal(RefusalReason.MissingParameter, decision.Refusal);
    }

    // -- three limit tiers --------------------------------------------------------------------

    [Fact]
    public void A_charter_cannot_widen_the_hardware_on_time()
    {
        _h.AcceptCharter(Now, c => c.Limits[KernelHarness.Relay] = new CapabilityLimits { MaxOnSeconds = 600 });

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 600), Now);

        Assert.True(decision.Authorized);
        Assert.True(decision.Clamped);
        Assert.Equal(30, decision.EffectiveParameters["on_s"]);
    }

    [Fact]
    public void A_charter_that_narrows_is_obeyed()
    {
        _h.AcceptCharter(Now, c => c.Limits[KernelHarness.Relay] = new CapabilityLimits { MaxOnSeconds = 10 });

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 25), Now);

        Assert.True(decision.Clamped);
        Assert.Equal(10, decision.EffectiveParameters["on_s"]);
    }

    [Fact]
    public void The_device_manifest_narrows_hardware_and_the_charter_cannot_undo_it()
    {
        _h.Authority.ApplyManifest(new MoundManifest
        {
            ManifestId = "mf-1",
            MoundId = KernelHarness.MoundId,
            IssuedAt = Now.ToWire(),
            DeviceLimits = { [KernelHarness.Relay] = new CapabilityLimits { MaxOnSeconds = 8 } },
            SafeState = "all_actuators_off"
        });

        _h.AcceptCharter(Now, c => c.Limits[KernelHarness.Relay] = new CapabilityLimits { MaxOnSeconds = 25 });

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 25), Now);

        Assert.Equal(8, decision.EffectiveParameters["on_s"]);
        Assert.Equal(8, decision.EffectiveLimits.MaxOnSeconds);
    }

    [Fact]
    public void A_charter_cannot_shorten_the_hardwares_required_off_time()
    {
        _h.AcceptCharter(Now, c =>
            c.Limits[KernelHarness.Relay] = new CapabilityLimits { MinOffSeconds = 5 });

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(300, decision.EffectiveLimits.MinOffSeconds);
    }

    [Fact]
    public void A_request_inside_every_limit_is_not_clamped()
    {
        _h.AcceptCharter(Now);

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 12), Now);

        Assert.False(decision.Clamped);
        Assert.Equal(12, decision.EffectiveParameters["on_s"]);
        Assert.Equal("", decision.Detail);
    }

    [Fact]
    public void A_clamp_names_the_limit_that_narrowed_it()
    {
        _h.AcceptCharter(Now);

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 600), Now);

        Assert.Equal(ActionOutcomes.Clamped, record.Outcome);
        Assert.Contains("max_on_s", record.Detail);
        Assert.Equal(600, record.RequestedParameters["on_s"]);
        Assert.Equal(30, record.Parameters["on_s"]);
    }

    [Fact]
    public void A_routines_own_compiled_limit_applies_too()
    {
        _h.AcceptCharter(Now);

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.WaterCycle, 60), Now);

        Assert.True(decision.Clamped);
        Assert.Equal(20, decision.EffectiveParameters["on_s"]);
    }

    // -- duty cycle and rate ------------------------------------------------------------------

    [Fact]
    public void The_duty_cycle_refuses_a_second_actuation_too_soon()
    {
        _h.AcceptCharter(Now);
        _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), Now.AddSeconds(30));

        Assert.Equal(RefusalReason.DutyCycle, decision.Refusal);
        Assert.Contains("min_off_s", decision.Detail);
    }

    [Fact]
    public void The_duty_cycle_clears_once_the_off_time_has_elapsed()
    {
        _h.AcceptCharter(Now);
        _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        var later = Now.AddSeconds(400);
        _h.Authority.RenewLease(later);

        Assert.True(_h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), later).Authorized);
    }

    [Fact]
    public void The_hourly_rate_refuses_the_run_past_it()
    {
        _h.AcceptCharter(Now, c =>
            c.Limits[KernelHarness.Relay] = new CapabilityLimits { MinOffSeconds = 0 });

        // min_off_s cannot be narrowed away (firmware says 300), so step past it each time.
        var at = Now;
        for (var i = 0; i < 6; i++)
        {
            at = Now.AddSeconds(i * 350);
            _h.Authority.RenewLease(at);
            var accepted = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), at);
            Assert.NotEqual(ActionOutcomes.Refused, accepted.Outcome);
        }

        var next = at.AddSeconds(350);
        _h.Authority.RenewLease(next);
        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), next);

        Assert.Equal(RefusalReason.RateLimit, decision.Refusal);
    }

    [Fact]
    public void Sensing_does_not_spend_the_relays_budget()
    {
        _h.AcceptCharter(Now);

        for (var i = 0; i < 20; i++)
            _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Sensor), Now.AddSeconds(i));

        Assert.Equal(0, _h.Kernel.History.StartsInTrailingHour(KernelHarness.Sensor, Now.AddSeconds(30)));
        Assert.True(_h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), Now).Authorized);
    }

    [Fact]
    public void Authorize_has_no_side_effects()
    {
        _h.AcceptCharter(Now);

        for (var i = 0; i < 50; i++)
            _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(0, _h.RelayExecutor.Calls);
        Assert.Equal(0, _h.Kernel.History.StartsInTrailingHour(KernelHarness.Relay, Now));
        Assert.True(_h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), Now).Authorized);
    }

    // -- execution and evidence ---------------------------------------------------------------

    [Fact]
    public void A_bound_executor_receives_effective_parameters_never_requested_ones()
    {
        _h.AcceptCharter(Now);

        _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 600), Now);

        Assert.Equal(30, _h.RelayExecutor.LastExecution!.Parameters["on_s"]);
    }

    [Fact]
    public void An_authorized_capability_with_no_executor_is_refused_not_assumed_done()
    {
        var capabilities = new CapabilityRegistry();
        capabilities.Register(new CapabilityDescriptor { Id = KernelHarness.Sensor, Class = ActionClass.Observe });
        var kernel = new CapabilityKernel(capabilities, new RoutineRegistry(capabilities),
            new KernelAuthority(KernelHarness.MoundId));

        var decision = kernel.Authorize(new CapabilityRequest { Capability = KernelHarness.Sensor }, Now);

        Assert.Equal(RefusalReason.ExecutorMissing, decision.Refusal);
    }

    [Fact]
    public void A_driver_fault_is_reported_as_failed_with_its_reason()
    {
        _h.AcceptCharter(Now);
        _h.RelayExecutor.Faults = true;

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(ActionOutcomes.Failed, record.Outcome);
        Assert.Contains("driver_fault", record.Detail);
        Assert.Contains("did not latch", record.Detail);
    }

    [Fact]
    public void A_driver_that_throws_does_not_escape_the_kernel()
    {
        _h.AcceptCharter(Now);
        _h.RelayExecutor.Throws = true;

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(ActionOutcomes.Failed, record.Outcome);
        Assert.Contains("i2c bus timeout", record.Detail);
    }

    [Fact]
    public void A_failed_actuation_still_spends_its_duty_cycle()
    {
        _h.AcceptCharter(Now);
        _h.RelayExecutor.Faults = true;
        _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);
        _h.RelayExecutor.Faults = false;

        var decision = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), Now.AddSeconds(30));

        Assert.Equal(RefusalReason.DutyCycle, decision.Refusal);
    }

    [Fact]
    public void A_blind_mound_reports_unverified_rather_than_success()
    {
        _h.AcceptCharter(Now);
        _h.RelayExecutor.ProducesEvidence = false;

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(ActionOutcomes.Unverified, record.Outcome);
        Assert.Equal(1, _h.RelayExecutor.Calls); // the relay really did fire
        Assert.True(record.EvidenceRequired);
    }

    [Fact]
    public void Stale_evidence_does_not_prove_a_fresh_action()
    {
        _h.AcceptCharter(Now);
        _h.RelayExecutor.EvidenceAge = TimeSpan.FromSeconds(600);

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(ActionOutcomes.Unverified, record.Outcome);
        Assert.Contains("stale", record.Detail);
    }

    [Fact]
    public void A_sighted_mound_proves_its_own_actuation()
    {
        _h.AcceptCharter(Now);

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(ActionOutcomes.Succeeded, record.Outcome);
        Assert.Single(record.EvidenceRefs);
    }

    [Fact]
    public void A_refusal_needs_no_proof_and_is_never_demoted_to_unverified()
    {
        _h.AcceptCharter(Now, c => c.ActionCeiling = "observe");

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(ActionOutcomes.Refused, record.Outcome);
        Assert.Empty(record.EvidenceRefs);
    }

    [Fact]
    public void Every_record_carries_the_mission_it_ran_under()
    {
        _h.AcceptCharter(Now);
        var request = new CapabilityRequest
        {
            Capability = KernelHarness.Relay,
            MissionId = "mission-77",
            Parameters = { ["on_s"] = 5 }
        };

        var record = _h.Kernel.Execute(request, Now);

        Assert.Equal("mission-77", record.MissionId);
        Assert.Equal("c-0001", record.CharterId);
    }

    [Fact]
    public void A_routine_record_carries_the_routine_id_so_evidence_patterns_see_it()
    {
        _h.AcceptCharter(Now);

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.WaterCycle, 5), Now);

        Assert.Equal(KernelHarness.WaterCycle, record.RoutineId);
        Assert.Equal(KernelHarness.WaterCycle, record.Capability);
        Assert.True(record.EvidenceRequired); // matched by the charter's "routine.*" pattern
    }
}
