using Micromound.Capabilities;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// P0.5, `v0.9.38`: a wall clock that is STEPPED must not hand back an operating budget.
///
/// <para>A duty cycle and a rate limit are both answers to "has enough time passed?", and both were
/// computed by subtracting two readings of a clock that can move for reasons other than time
/// passing. The canonical case is not exotic: a Pi or an ESP32 with no battery-backed RTC boots
/// believing it is 1970, and the first NTP or controller sync steps it forward by decades. At that
/// instant every cooldown reads as elapsed and every rate window as empty — at the exact moment the
/// mound has the least reason to trust its own sense of time.</para>
///
/// <para>The guard is to record a monotonic stamp alongside each wall instant and believe whichever
/// clock claims LESS time passed. These tests hold both ends of that: the step is not credited, and
/// real elapsed time still is — a guard that simply refused forever would be no better than the
/// hazard it replaced, and would be just as green.</para>
/// </summary>
public class SteppedClockTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    /// <summary>A monotonic counter the test advances by hand — real elapsed time, decoupled from the wall clock.</summary>
    private sealed class FakeMonotonic : TimeProvider
    {
        private long _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class Relay : ICapabilityExecutor
    {
        public string CapabilityId => "act.relay_1";
        public bool IsAvailable => true;
        public int Runs { get; private set; }

        public ExecutionOutcome Execute(CapabilityExecution execution)
        {
            Runs++;
            return ExecutionOutcome.Ok([], execution.StartedAt.AddSeconds(1));
        }
    }

    private static Charter Charter(double minOff, double maxRate) => new()
    {
        CharterId = "c-1", MoundId = "mm-1", MissionRef = "m",
        IssuedAt = T0.ToWire(), ExpiresAt = T0.AddYears(50).ToWire(),
        LeaseTtlSeconds = 1_000_000_000, ActionCeiling = "benign",
        Capabilities = ["act.relay_1"],
        Limits = { ["act.relay_1"] = new CapabilityLimits { MinOffSeconds = minOff, MaxRatePerHour = maxRate } },
        SafeState = "all_actuators_off"
    };

    private static (CapabilityKernel kernel, ActuationHistory history, Relay relay) Mound(
        TimeProvider? time, double minOff = 300, double maxRate = 2)
    {
        var caps = new CapabilityRegistry();
        caps.Register(new CapabilityDescriptor
        {
            Id = "act.relay_1", Class = ActionClass.Benign,
            HardwareLimits = new CapabilityLimits { MinOffSeconds = minOff, MaxRatePerHour = maxRate }
        });
        var history = new ActuationHistory { Time = time };
        var authority = new KernelAuthority("mm-1");
        authority.AcceptCharter(Charter(minOff, maxRate), T0);
        var kernel = new CapabilityKernel(caps, new RoutineRegistry(caps), authority, history);
        var relay = new Relay();
        kernel.RegisterExecutor(relay);
        return (kernel, history, relay);
    }

    private static CapabilityRequest Act() => new() { Capability = "act.relay_1" };

    /// <remarks>
    /// The cooldown. Five seconds of real time pass; the wall clock claims an hour. Before this the
    /// kernel believed the wall clock, so the second actuation ran 5 s after the first on a relay
    /// whose compiled minimum off-time is 300 s.
    /// </remarks>
    [Fact]
    public void A_forward_clock_step_does_not_hand_back_a_cooldown()
    {
        var clock = new FakeMonotonic();
        var (kernel, _, relay) = Mound(clock);

        kernel.Execute(Act(), T0);
        clock.Advance(TimeSpan.FromSeconds(5));               // five seconds really pass…
        var decision = kernel.Authorize(Act(), T0.AddHours(1));   // …and the RTC is corrected an hour forward

        Assert.False(decision.Authorized);
        Assert.Equal(RefusalReason.DutyCycle, decision.Refusal);
        kernel.Execute(Act(), T0.AddHours(1));
        Assert.Equal(1, relay.Runs);
    }

    /// <remarks>
    /// The rate window, which fails the same way for a different reason: a trailing hour measured
    /// against a stepped clock ages every start out of it at once, so a budget of two per hour
    /// becomes unbounded — one step, one more actuation, repeat.
    /// </remarks>
    [Fact]
    public void A_forward_clock_step_does_not_refresh_a_rate_budget()
    {
        var clock = new FakeMonotonic();
        var (kernel, _, relay) = Mound(clock, minOff: 0, maxRate: 2);

        kernel.Execute(Act(), T0);
        clock.Advance(TimeSpan.FromSeconds(10));
        kernel.Execute(Act(), T0.AddSeconds(10));
        Assert.Equal(2, relay.Runs);                          // the budget is spent

        clock.Advance(TimeSpan.FromSeconds(10));              // twenty seconds of real time in all
        var decision = kernel.Authorize(Act(), T0.AddHours(3));   // the clock claims three hours

        Assert.False(decision.Authorized);
        Assert.Equal(RefusalReason.RateLimit, decision.Refusal);
    }

    /// <remarks>
    /// The other half, and the one that stops this being a blunt refusal: when the time really has
    /// passed, both clocks agree and the mound acts. A guard that never let the budget recover would
    /// pass the two tests above and be worse than the defect.
    /// </remarks>
    [Fact]
    public void Real_elapsed_time_still_clears_the_cooldown()
    {
        var clock = new FakeMonotonic();
        var (kernel, _, relay) = Mound(clock);

        kernel.Execute(Act(), T0);
        clock.Advance(TimeSpan.FromSeconds(400));             // 400 s really pass
        kernel.Execute(Act(), T0.AddSeconds(400));            // and the wall clock agrees

        Assert.Equal(2, relay.Runs);
    }

    /// <remarks>
    /// A step BACKWARD was already safe and stays safe: the wall clock then claims less than the
    /// monotonic counter, and the smaller answer is the one taken either way. Worth pinning because
    /// an earlier attempt at this slice (`v0.9.33`) guarded only this direction — the one that was
    /// never the risk — and its tests passed with the mechanism reverted.
    /// </remarks>
    [Fact]
    public void A_backward_clock_step_is_conservative_in_both_designs()
    {
        var clock = new FakeMonotonic();
        var (kernel, _, _) = Mound(clock);

        kernel.Execute(Act(), T0);
        clock.Advance(TimeSpan.FromSeconds(400));
        var decision = kernel.Authorize(Act(), T0.AddSeconds(-3600));   // the clock is corrected backwards

        Assert.False(decision.Authorized);
        Assert.Equal(RefusalReason.DutyCycle, decision.Refusal);
    }

    /// <remarks>
    /// With no monotonic source there is nothing to cross-check against, and the wall clock is all
    /// there is. This is the default, and it is what every fake-clock fixture and bench gets — so it
    /// is pinned rather than left to be discovered. It is also exactly the state of a restored
    /// history, which is the residual P0.5 still names.
    /// </remarks>
    [Fact]
    public void Without_a_monotonic_source_a_stepped_clock_is_believed()
    {
        var (kernel, _, relay) = Mound(time: null);

        kernel.Execute(Act(), T0);
        kernel.Execute(Act(), T0.AddHours(1));

        Assert.Equal(2, relay.Runs);
    }

    /// <remarks>
    /// Stamps do not survive the process that made them, so a restored entry ages on the wall clock
    /// alone. Recorded as a property rather than a gap to be surprised by later: the gap across a
    /// restart is unknowable from inside the mound, and closing it needs the controller.
    /// </remarks>
    [Fact]
    public void A_restored_entry_carries_no_monotonic_evidence()
    {
        var clock = new FakeMonotonic();
        var written = new ActuationHistory { Time = clock };
        written.Record("act.relay_1", T0, T0.AddSeconds(1));

        var restored = new ActuationHistory { Time = clock };
        restored.Restore(written.Snapshot(T0.AddSeconds(1)));

        // The writing history still refuses: no real time has passed on the monotonic counter.
        Assert.False(written.MinOffElapsed("act.relay_1", 300, T0.AddHours(1)));

        // The restored one has only the wall clock to go on, and says so by believing it.
        Assert.True(restored.MinOffElapsed("act.relay_1", 300, T0.AddHours(1)));
        Assert.Equal(T0.AddSeconds(1), restored.LastEnd("act.relay_1"));
    }
}
