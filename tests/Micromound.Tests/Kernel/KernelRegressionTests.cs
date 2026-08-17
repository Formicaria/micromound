using Micromound.Capabilities;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// Cases where an earlier version of the kernel was wrong. Each one is a way authority could have
/// been widened without anybody sending a charter that said so.
/// </summary>
public class KernelRegressionTests
{
    private readonly KernelHarness _h = new();
    private static DateTimeOffset Now => KernelHarness.Now;

    [Fact]
    public void A_routine_cannot_run_inside_the_relays_own_compiled_cooldown()
    {
        _h.AcceptCharter(Now);

        // The routine declares only max_on_s. The relay it drives declares min_off_s 300.
        var first = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.WaterCycle, 5), Now);
        Assert.NotEqual(ActionOutcomes.Refused, first.Outcome);

        var again = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.WaterCycle, 5), Now.AddSeconds(30));

        Assert.Equal(RefusalReason.DutyCycle, again.Refusal);
    }

    [Fact]
    public void A_routine_spends_the_budget_of_the_capability_it_drives()
    {
        _h.AcceptCharter(Now);
        _h.Kernel.Execute(KernelHarness.Request(KernelHarness.WaterCycle, 5), Now);

        // Going around the routine must not find a fresh relay.
        var direct = _h.Kernel.Authorize(KernelHarness.Request(KernelHarness.Relay, 5), Now.AddSeconds(30));

        Assert.Equal(RefusalReason.DutyCycle, direct.Refusal);
        Assert.Contains(KernelHarness.Relay, direct.Detail);
    }

    [Fact]
    public void A_routine_cannot_widen_the_bound_of_the_capability_it_drives()
    {
        var capabilities = new CapabilityRegistry();
        capabilities.Register(new CapabilityDescriptor
        {
            Id = "act.pump",
            Class = ActionClass.Benign,
            HardwareLimits = new CapabilityLimits { MaxOnSeconds = 5 },
            Parameters = new HashSet<string>(StringComparer.Ordinal) { "on_s" },
            DurationParameter = "on_s"
        });

        var routines = new RoutineRegistry(capabilities);
        routines.Register(new RoutineDescriptor
        {
            Id = "routine.long_soak",
            Class = ActionClass.Benign,
            RequiredCapabilities = ["act.pump"],
            HardwareLimits = new CapabilityLimits { MaxOnSeconds = 600 }, // generous, and inert
            Parameters = new HashSet<string>(StringComparer.Ordinal) { "on_s" },
            DurationParameter = "on_s"
        });

        var authority = new KernelAuthority(KernelHarness.MoundId);
        var kernel = new CapabilityKernel(capabilities, routines, authority);
        kernel.RegisterExecutor(new FakeExecutor("act.pump"));
        kernel.RegisterExecutor(new FakeExecutor("routine.long_soak"));

        authority.AcceptCharter(new Charter
        {
            CharterId = "c-1",
            MoundId = KernelHarness.MoundId,
            IssuedAt = Now.ToWire(),
            ExpiresAt = Now.AddHours(1).ToWire(),
            LeaseTtlSeconds = 900,
            ActionCeiling = "benign",
            Capabilities = ["act.pump"],
            Routines = ["routine.long_soak"],
            SafeState = "all_actuators_off",
            SyncIntervalSeconds = 15
        }, Now, capabilities.DeclaredCapabilities(), routines.DeclaredRoutines());

        var decision = kernel.Authorize(new CapabilityRequest
        {
            Capability = "routine.long_soak",
            Parameters = { ["on_s"] = 600 }
        }, Now);

        Assert.True(decision.Clamped);
        Assert.Equal(5, decision.EffectiveParameters["on_s"]);
    }

    [Fact]
    public void An_actuator_that_forgot_to_declare_its_class_is_refused_at_registration()
    {
        var registry = new CapabilityRegistry();

        // Class defaults to Observe. Left unchecked this would be executable with no charter and
        // exempt from duty-cycle and rate limits, which apply only above Observe.
        var result = registry.Register(new CapabilityDescriptor { Id = "act.relay" });

        Assert.False(result.IsValid);
        Assert.False(registry.Contains("act.relay"));
    }

    [Fact]
    public void Reading_the_rate_window_does_not_consume_it()
    {
        var history = new ActuationHistory();
        history.Record("act.pump", Now, Now.AddSeconds(1));

        // A clock that jumped forward and was then corrected must not have erased the record.
        Assert.Equal(0, history.StartsInTrailingHour("act.pump", Now.AddHours(5)));
        Assert.Equal(1, history.StartsInTrailingHour("act.pump", Now.AddSeconds(30)));
    }

    [Fact]
    public void A_refusal_still_reports_whether_evidence_was_required()
    {
        _h.AcceptCharter(Now, c => c.ActionCeiling = "observe");

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 5), Now);

        Assert.Equal(ActionOutcomes.Refused, record.Outcome);
        Assert.True(record.EvidenceRequired); // the charter's "act.*" policy covers it
    }

    [Fact]
    public void A_clamp_demoted_to_unverified_still_names_the_limit_that_clamped_it()
    {
        _h.AcceptCharter(Now);
        _h.RelayExecutor.ProducesEvidence = false;

        var record = _h.Kernel.Execute(KernelHarness.Request(KernelHarness.Relay, 600), Now);

        Assert.Equal(ActionOutcomes.Unverified, record.Outcome);
        Assert.Contains("max_on_s", record.Detail);
        Assert.Contains("no evidence", record.Detail);
    }
}
