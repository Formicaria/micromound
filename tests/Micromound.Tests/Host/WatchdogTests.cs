using Micromound.Host;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The independent watchdog. <see cref="LoopWatchdog"/> — the pure timing core — is tested against a
/// fed clock with no threads at all. <see cref="WatchdogThread"/> — the one concurrent piece — is
/// tested on its real background thread but with an INJECTED clock, so "time" advances by assignment
/// rather than by sleeping through a real timeout: the thread polls on a tiny real cadence, the test
/// jumps the clock, and the fire is observed within a bounded wait. Deterministic, and fast.
/// </summary>
public sealed class WatchdogTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

    // --- LoopWatchdog: pure timing, fed clock ---

    [Fact]
    public void Before_the_timeout_it_is_not_unresponsive()
    {
        var wd = new LoopWatchdog(TimeSpan.FromSeconds(10), T0);
        Assert.False(wd.CheckUnresponsive(T0.AddSeconds(10)));   // exactly at the boundary is still alive
        Assert.False(wd.HasFired);
    }

    [Fact]
    public void Past_the_timeout_with_no_kick_it_fires()
    {
        var wd = new LoopWatchdog(TimeSpan.FromSeconds(10), T0);
        Assert.True(wd.CheckUnresponsive(T0.AddSeconds(10.001)));
        Assert.True(wd.HasFired);
    }

    [Fact]
    public void A_kick_pushes_the_deadline_forward()
    {
        var wd = new LoopWatchdog(TimeSpan.FromSeconds(10), T0);
        wd.Kick(T0.AddSeconds(8));                                // alive again at t=8
        Assert.False(wd.CheckUnresponsive(T0.AddSeconds(17)));    // 17 - 8 = 9 <= 10: still alive
        Assert.True(wd.CheckUnresponsive(T0.AddSeconds(19)));     // 19 - 8 = 11 > 10: unresponsive
    }

    [Fact]
    public void It_fires_exactly_once_and_latches()
    {
        var wd = new LoopWatchdog(TimeSpan.FromSeconds(10), T0);
        Assert.True(wd.CheckUnresponsive(T0.AddSeconds(20)));     // first: fires
        Assert.False(wd.CheckUnresponsive(T0.AddSeconds(30)));    // latched: never again
        Assert.True(wd.HasFired);
    }

    [Fact]
    public void A_kick_after_firing_does_not_un_fire_the_sticky_stop()
    {
        var wd = new LoopWatchdog(TimeSpan.FromSeconds(10), T0);
        wd.CheckUnresponsive(T0.AddSeconds(20));                  // fires
        wd.Kick(T0.AddSeconds(21));                               // the loop "comes back" — ignored
        Assert.True(wd.HasFired);
        Assert.False(wd.CheckUnresponsive(T0.AddSeconds(22)));    // stays fired, does not re-fire
    }

    [Fact]
    public void A_backwards_clock_reading_never_moves_the_deadline_back()
    {
        var wd = new LoopWatchdog(TimeSpan.FromSeconds(10), T0.AddSeconds(50));
        wd.Kick(T0.AddSeconds(10));                               // a stale/earlier reading: ignored
        Assert.False(wd.CheckUnresponsive(T0.AddSeconds(55)));    // still measured from t=50, not t=10
    }

    [Fact]
    public void A_non_positive_timeout_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopWatchdog(TimeSpan.Zero, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopWatchdog(TimeSpan.FromSeconds(-1), T0));
    }

    // --- WatchdogThread: real thread, injected clock ---

    /// <summary>A clock a test moves by assignment; read from the watchdog thread, written from the test.</summary>
    private sealed class TestClock(DateTimeOffset start)
    {
        private long _ticks = start.UtcTicks;
        public DateTimeOffset Now
        {
            get => new(Volatile.Read(ref _ticks), TimeSpan.Zero);
            set => Volatile.Write(ref _ticks, value.UtcTicks);
        }
    }

    [Fact]
    public void The_thread_fires_the_action_once_the_clock_passes_the_timeout()
    {
        var clock = new TestClock(T0);
        using var fired = new ManualResetEventSlim(false);
        using var wd = new WatchdogThread(TimeSpan.FromSeconds(10), () => fired.Set(),
            pollInterval: TimeSpan.FromMilliseconds(20), clock: () => clock.Now);
        wd.Start();

        Assert.False(fired.Wait(150));          // no time has passed: it must not fire
        clock.Now = T0.AddSeconds(11);          // jump past the 10s timeout
        Assert.True(fired.Wait(2000));          // the next poll (within ~20ms) fires
        Assert.True(wd.HasFired);
    }

    [Fact]
    public void Regular_kicks_keep_the_thread_from_firing()
    {
        var clock = new TestClock(T0);
        var fired = 0;
        using var wd = new WatchdogThread(TimeSpan.FromSeconds(10), () => Interlocked.Increment(ref fired),
            pollInterval: TimeSpan.FromMilliseconds(20), clock: () => clock.Now);
        wd.Start();

        // Advance well past a single timeout in total, but kick within it each step.
        for (var s = 1; s <= 30; s++)
        {
            clock.Now = T0.AddSeconds(s);
            wd.Kick();
            Thread.Sleep(10);
        }
        Assert.Equal(0, Volatile.Read(ref fired));   // never judged unresponsive while kicking
    }

    [Fact]
    public void Dispose_stops_the_thread_and_is_safe_to_repeat()
    {
        var clock = new TestClock(T0);
        var wd = new WatchdogThread(TimeSpan.FromSeconds(10), () => { },
            pollInterval: TimeSpan.FromMilliseconds(20), clock: () => clock.Now);
        wd.Start();
        wd.Dispose();
        wd.Dispose();   // idempotent

        clock.Now = T0.AddSeconds(100);   // even far past the timeout, a disposed watchdog does nothing
        Thread.Sleep(60);
        Assert.False(wd.HasFired);
    }
}
