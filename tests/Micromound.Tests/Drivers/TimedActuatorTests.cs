using Micromound.Capabilities;
using Micromound.Drivers;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The digital actuator's timed hold: an execution drives the line active and holds it for <c>on_s</c>,
/// and <see cref="ITimedDriver.ServiceHolds"/> — the clock-driven half the service loop calls each
/// tick — releases it when the duration elapses. These prove the hold is bounded, released on safe
/// state, idempotent, and fail-safe when the release write itself throws (the case a real GPIO line
/// introduced). Driven by an injected clock, no host and no real timer.
/// </summary>
public sealed class TimedActuatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

    private static DigitalActuatorDriver Configured(IDigitalOutput line, double maxOnS = 30, bool activeHigh = true)
    {
        var driver = new DigitalActuatorDriver(line);
        driver.Configure(new Dictionary<string, string>
        {
            ["capability"] = "act.water_valve",
            ["max_on_s"] = maxOnS.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["active_high"] = activeHigh ? "true" : "false"
        });
        return driver;
    }

    private static ExecutionOutcome Actuate(DigitalActuatorDriver driver, double onS, double maxOnS = 30, DateTimeOffset? at = null) =>
        driver.Executors[0].Execute(new CapabilityExecution
        {
            CapabilityId = "act.water_valve",
            Parameters = new Dictionary<string, double> { ["on_s"] = onS },
            StartedAt = at ?? Now,
            EffectiveLimits = new CapabilityLimits { MaxOnSeconds = maxOnS }
        });

    [Fact]
    public void A_hold_stays_active_up_to_the_deadline_and_releases_after()
    {
        var line = new InMemoryDigitalOutput();
        var driver = Configured(line);

        Assert.True(Actuate(driver, onS: 10).Succeeded);
        Assert.True(line.State);
        Assert.True(driver.IsHolding);

        driver.ServiceHolds(Now.AddSeconds(9.999));   // just before
        Assert.True(line.State);

        driver.ServiceHolds(Now.AddSeconds(10));       // at the deadline
        Assert.False(line.State);
        Assert.False(driver.IsHolding);
    }

    [Fact]
    public void The_hold_is_capped_at_the_effective_max_even_if_a_longer_on_s_slips_through()
    {
        // on_s arrives already clamped by the kernel; the driver caps it again at the effective bound
        // as a last-resort belt, so a contract violation upstream can never hold beyond the hardware.
        var line = new InMemoryDigitalOutput();
        var driver = Configured(line, maxOnS: 10);

        Actuate(driver, onS: 100, maxOnS: 10);
        Assert.Equal(10, driver.LastOnSeconds);

        driver.ServiceHolds(Now.AddSeconds(10));   // released at the cap, not at 100
        Assert.False(line.State);
    }

    [Theory]
    [InlineData(0)]                 // zero is not a hold
    [InlineData(-5)]                // a negative duration is nonsense
    [InlineData(double.NaN)]        // NaN is never capped (NaN > max is false) and is never a deadline
    public void A_non_positive_or_nan_duration_always_faults_and_holds_nothing(double bad)
    {
        var line = new InMemoryDigitalOutput();
        var driver = Configured(line, maxOnS: 30);   // even under a finite bound these fault

        var outcome = Actuate(driver, onS: bad, maxOnS: 30);
        Assert.False(outcome.Succeeded);
        Assert.False(driver.IsHolding);
        Assert.False(line.State);
    }

    [Fact]
    public void An_infinite_duration_with_no_bound_faults()
    {
        // With no effective max_on_s there is nothing to cap an infinite request against, so it is a
        // fault — never an unbounded hold.
        var line = new InMemoryDigitalOutput();
        var driver = Configured(line);

        var outcome = driver.Executors[0].Execute(new CapabilityExecution
        {
            CapabilityId = "act.water_valve",
            Parameters = new Dictionary<string, double> { ["on_s"] = double.PositiveInfinity },
            StartedAt = Now,
            EffectiveLimits = new CapabilityLimits()   // no MaxOnSeconds bound
        });

        Assert.False(outcome.Succeeded);
        Assert.False(driver.IsHolding);
        Assert.False(line.State);
    }

    [Fact]
    public void An_infinite_request_under_a_finite_max_is_capped_not_faulted()
    {
        // Defense in depth: even an infinite request cannot hold beyond the hardware bound — it is
        // clamped to the effective max, so the line is held for exactly that and then released.
        var line = new InMemoryDigitalOutput();
        var driver = Configured(line, maxOnS: 10);

        var outcome = Actuate(driver, onS: double.PositiveInfinity, maxOnS: 10);
        Assert.True(outcome.Succeeded);
        Assert.Equal(10, driver.LastOnSeconds);
        Assert.True(line.State);

        driver.ServiceHolds(Now.AddSeconds(10));
        Assert.False(line.State);
    }

    [Fact]
    public void Service_holds_is_idempotent_before_any_actuation_and_after_release()
    {
        var line = new InMemoryDigitalOutput();
        var driver = Configured(line);

        driver.ServiceHolds(Now);                 // no hold: no-op
        Assert.False(line.State);

        Actuate(driver, onS: 5);
        driver.ServiceHolds(Now.AddSeconds(5));   // release
        var writesAfterRelease = line.Writes;
        driver.ServiceHolds(Now.AddSeconds(9));   // already released: no further write
        Assert.Equal(writesAfterRelease, line.Writes);
    }

    [Fact]
    public void Entering_safe_state_mid_hold_releases_now_and_ends_the_hold()
    {
        var line = new InMemoryDigitalOutput();
        var driver = Configured(line);

        Actuate(driver, onS: 30);
        Assert.True(line.State);

        driver.EnterSafeState();                  // a stop/quiesce/shutdown mid-hold
        Assert.False(line.State);
        Assert.False(driver.IsHolding);

        var writes = line.Writes;
        driver.ServiceHolds(Now.AddSeconds(30));   // the elapsed deadline now finds no hold: no re-drive
        Assert.Equal(writes, line.Writes);
    }

    [Fact]
    public void An_active_low_actuator_holds_low_and_releases_high()
    {
        var line = new InMemoryDigitalOutput();
        var driver = Configured(line, activeHigh: false);   // active level is LOW

        Actuate(driver, onS: 10);
        Assert.False(line.State);                 // held at its active (low) level

        driver.ServiceHolds(Now.AddSeconds(10));
        Assert.True(line.State);                  // released to its safe (high) level
    }

    // --- A real port's release write can THROW; the hold must stay pending and the failure surface ---

    /// <summary>A line that throws the Nth time it is driven to the safe (low) level.</summary>
    private sealed class ThrowsOnNthLow(int n) : IDigitalOutput
    {
        private int _lows;
        public bool State { get; private set; }
        public void Write(bool high)
        {
            if (!high && ++_lows == n)
                throw new IOException("simulated GPIO release failure");
            State = high;
        }
    }

    [Fact]
    public void A_release_write_that_throws_leaves_the_hold_pending_and_propagates()
    {
        // Low #1 = configure's initial safe write (must succeed); low #2 = the release (throws).
        var line = new ThrowsOnNthLow(n: 2);
        var driver = Configured(line);

        Actuate(driver, onS: 5);
        Assert.True(line.State);   // energized/held

        // The deadline release throws — ServiceHolds propagates (the host turns this into a trip) and
        // the hold is NOT cleared, so a later tick tries again.
        Assert.ThrowsAny<IOException>(() => driver.ServiceHolds(Now.AddSeconds(5)));
        Assert.True(driver.IsHolding);
        Assert.True(line.State);   // still hot — could not be released

        // A subsequent tick (the transient cleared) releases it and ends the hold.
        driver.ServiceHolds(Now.AddSeconds(6));
        Assert.False(line.State);
        Assert.False(driver.IsHolding);
    }
}
