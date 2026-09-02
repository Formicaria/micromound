namespace Micromound.Host;

/// <summary>
/// The thin thread around <see cref="LoopWatchdog"/> — the one genuinely concurrent piece of the
/// daemon. It runs on its OWN background thread (not the service loop's, and not a thread-pool thread
/// that a blocked continuation could starve), waking on a short cadence to ask the watchdog whether
/// the loop has gone silent. All the logic lives in <see cref="LoopWatchdog"/>, which a fake clock
/// tests exhaustively; this wrapper is deliberately almost empty so there is little here that a real
/// timer test cannot cover.
///
/// <para>The loop calls <see cref="Kick"/> after each completed tick. If the kicks stop for the whole
/// timeout, the watchdog fires <c>onUnresponsive</c> exactly once — the action that de-energizes and
/// stops the mound (<see cref="MoundHost.WatchdogStop"/>). The action runs ON the watchdog thread, so
/// it must be thread-safe against the loop; that is why the host serialises its safe-state path.</para>
/// </summary>
public sealed class WatchdogThread : IDisposable
{
    private readonly LoopWatchdog _watchdog;
    private readonly Action _onUnresponsive;
    private readonly TimeSpan _pollInterval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Thread _thread;
    private volatile bool _running;

    /// <param name="timeout">Loop-silence timeout handed to the <see cref="LoopWatchdog"/>.</param>
    /// <param name="onUnresponsive">The de-energize-and-stop action, run once on the watchdog thread.</param>
    /// <param name="pollInterval">How often to check. Defaults to a quarter of the timeout (so several
    /// checks land within one timeout), floored at 250 ms and capped at 5 s.</param>
    /// <param name="clock">Time source; defaults to <see cref="DateTimeOffset.UtcNow"/>. Injectable for tests.</param>
    public WatchdogThread(TimeSpan timeout, Action onUnresponsive, TimeSpan? pollInterval = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(onUnresponsive);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _watchdog = new LoopWatchdog(timeout, _clock());
        _onUnresponsive = onUnresponsive;
        _pollInterval = pollInterval ?? Clamp(timeout / 4, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
        _thread = new Thread(Run) { IsBackground = true, Name = "micromound-watchdog" };
    }

    /// <summary>True once the watchdog has fired its stop action.</summary>
    public bool HasFired => _watchdog.HasFired;

    /// <summary>Begin watching. The loop must start kicking at least once per timeout from here.</summary>
    public void Start()
    {
        _running = true;
        _thread.Start();
    }

    /// <summary>The loop is alive: it completed a tick. Called by the service loop each iteration.</summary>
    public void Kick() => _watchdog.Kick(_clock());

    private void Run()
    {
        while (_running)
        {
            Thread.Sleep(_pollInterval);
            if (!_running)
                return;
            if (_watchdog.CheckUnresponsive(_clock()))
            {
                // Fire once. A throw here must not take down the watchdog thread silently — log it; the
                // mound is in an unknown state and that itself is the alarm. We keep looping afterwards
                // only to honour a clean Dispose; CheckUnresponsive has latched and will not fire again.
                try { _onUnresponsive(); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"micromound: watchdog stop action threw: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Stop watching and join the thread. Safe to call more than once.</summary>
    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(2));
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
