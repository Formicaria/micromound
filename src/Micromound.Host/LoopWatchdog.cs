namespace Micromound.Host;

/// <summary>
/// The pure timing core of the independent watchdog: a deadline that the service loop must keep
/// pushing forward by calling <see cref="Kick"/>, and a check that fires ONCE when the loop has gone
/// silent for longer than the timeout. It holds no thread and no clock — it is fed the current time —
/// so its whole decision is deterministic and unit-testable without a real timer.
///
/// <para><b>Why an independent watchdog at all.</b> Since v0.9.9 a digital actuation is <em>held</em>
/// for its duration, so a line is deliberately energized between service ticks. The kernel's stale-
/// heartbeat rule refuses NEW actuations while the loop is silent, but it cannot release a line
/// already held — only the loop's own tick sweep or a safe-state does that. If the loop hangs, nothing
/// on it runs, so a held line stays hot. This watchdog is the hardware-independent backstop: driven
/// from its own thread (<see cref="WatchdogThread"/>), it notices the loop has stopped kicking and
/// drives the mound to a de-energized, stopped state without needing the loop to cooperate.</para>
///
/// <para><b>Fires once, and the stop is sticky.</b> A fire means the loop was unresponsive for the
/// whole timeout — not a transient. It is latched here (a second call does not re-fire) and the action
/// it triggers is a persisted stop, because a loop that had to be rescued by the independent watchdog
/// is not to be trusted until an operator has looked. Set the timeout generously — several tick
/// intervals — so an ordinary GC or scheduling pause never trips it.</para>
/// </summary>
public sealed class LoopWatchdog
{
    private readonly object _lock = new();
    private readonly TimeSpan _timeout;
    private DateTimeOffset _lastKick;
    private bool _fired;

    /// <param name="timeout">How long the loop may go without a <see cref="Kick"/> before it is judged
    /// unresponsive. Must be positive.</param>
    /// <param name="startedAt">The moment the loop is considered last-alive at construction, so a slow
    /// first tick is measured from start, not from the epoch.</param>
    public LoopWatchdog(TimeSpan timeout, DateTimeOffset startedAt)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "the watchdog timeout must be positive");
        _timeout = timeout;
        _lastKick = startedAt;
    }

    /// <summary>The loop completed a tick and is alive as of <paramref name="now"/>. Idempotent and
    /// cheap; called every tick. A kick after the watchdog has already fired is ignored — the stop is
    /// sticky, so a loop that comes back from the dead does not un-stop itself.</summary>
    public void Kick(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_fired)
                return;
            if (now > _lastKick)   // never let a stale clock reading move the deadline backwards
                _lastKick = now;
        }
    }

    /// <summary>
    /// Has the loop been silent for longer than the timeout as of <paramref name="now"/>? Returns true
    /// EXACTLY ONCE — the first time it finds the loop unresponsive — and latches, so the caller fires
    /// its stop action a single time. Every later call returns false.
    /// </summary>
    public bool CheckUnresponsive(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_fired)
                return false;
            if (now - _lastKick <= _timeout)
                return false;
            _fired = true;
            return true;
        }
    }

    /// <summary>True once the watchdog has fired. For a test or a health view.</summary>
    public bool HasFired { get { lock (_lock) return _fired; } }
}
