using Micromound.Protocol;

namespace Micromound.Host;

/// <summary>
/// The service lifecycle around a <see cref="MoundHost"/>: the heartbeat, the sync beat, the
/// watchdog's response, and a graceful, safe shutdown. It is deliberately clock-driven —
/// <see cref="Tick"/> takes the current time rather than sleeping — so the loop's safety behaviour is
/// deterministic and testable without a real timer. The OS glue that calls <see cref="Tick"/> on a
/// cadence and <see cref="Shutdown"/> on a signal lives in the daemon entry point, not here.
///
/// <para><b>What the watchdog actually protects, and how.</b> A <em>sticky trip</em> (an interlock,
/// a thermal cut-out) is escalated by <see cref="Tick"/> to a persisted <em>stop</em> — de-energized
/// and durably halted, so a restart cannot clear it (a restart never clears a stop). A <em>stale
/// heartbeat</em> is self-healing and is NOT escalated: its protection is the kernel refusing every
/// actuation while the beat is stale, which holds even if this loop has stopped ticking. This class
/// does not add an independent timer thread, so it cannot itself de-energize a loop that has hung
/// mid-tick; that thread is a later slice. Within a running loop the kernel's per-actuation refusal
/// is the stale-heartbeat guarantee, and the trip escalation here is the physical one.</para>
/// </summary>
public sealed class MoundService(MoundHost host)
{
    private DateTimeOffset? _lastSync;

    public MoundHost Host => host;

    /// <summary>True when the watchdog demands safe state — a sticky safety trip or a stale heartbeat.</summary>
    public bool SafeStateEngaged => host.Guard.SafeStateRequired;

    /// <summary>
    /// The sync cadence the controller stated at ENROLLMENT (PROTOCOL.md §3's <c>sync_interval_s</c>) —
    /// the bootstrap value, in force until a charter arrives. Null — the default — means no controller
    /// cadence is known and the mound syncs on every tick.
    /// </summary>
    public TimeSpan? SyncInterval { get; set; }

    /// <summary>
    /// The cadence actually in force. The ACTIVE CHARTER's <c>sync_interval_s</c> (PROTOCOL.md §4) wins
    /// whenever the mound is chartered: it is the live authority, and the controller sets it from the
    /// same mound record it answered enrollment from, so it is simply the fresher copy — an operator who
    /// changes the cadence issues a new charter. Before any charter, the enrollment value applies. With
    /// neither, every tick syncs.
    ///
    /// <para><b>Deliberately NOT the tick interval.</b> The tick also releases elapsed actuation holds,
    /// kicks the independent watchdog, and refreshes the heartbeat; those must stay fast. A controller
    /// that asks to be synced every 60 s is choosing how often it hears from the mound — it is not
    /// asking for a valve's 5 s hold to be released 60 s late. So the cadence throttles the sync beat
    /// alone, and everything safety-bearing keeps the tick's own rhythm.</para>
    ///
    /// <para><b>Why honouring it matters, and its safety bound.</b> The controller judges a mound
    /// offline from this very cadence (missed beats × interval), so a mound syncing on its own schedule
    /// would be mis-reported. A cadence longer than the lease TTL is self-limiting: the mound cannot
    /// renew and quiesces — the fail-safe direction — so no cap is needed here; the lease is the bound.</para>
    /// </summary>
    public TimeSpan? EffectiveSyncInterval
    {
        get
        {
            var charter = host.Authority.ActiveCharter;
            if (charter is { SyncIntervalSeconds: > 0 })
                return TimeSpan.FromSeconds(charter.SyncIntervalSeconds);
            return SyncInterval;
        }
    }

    /// <summary>
    /// One service tick: mark the runtime alive, respond to any pending trip, run a sync beat, refresh
    /// the watchdog, release any elapsed actuation hold, and respond again. A sticky trip is escalated
    /// to a persisted stop (durable across a restart); any safe-state demand also physically
    /// de-energizes now. Idempotent.
    ///
    /// <para><b>Why the watchdog is answered FIRST, before sync.</b> The independent watchdog thread
    /// can record a trip (and drive a stop) while this loop is stuck. When the loop resumes, reading
    /// <c>Guard.HasTrip</c> here takes the Guard's own lock — a memory barrier — so the loop observes
    /// that trip and stops ITSELF before <see cref="MoundHost.Sync"/> could authorize a downlinked
    /// actuation on a stale, not-yet-stopped view of authority (a real window on the weak-memory ARM
    /// target). From that point everything is same-thread. The second response at the end catches a
    /// trip that AROSE during this tick — e.g. a driver that failed to release a hold.</para>
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        host.Beat(now);
        RespondToWatchdog(now);        // observe any watchdog/interlock trip first, via the Guard lock barrier
        if (SyncDue(now))
        {
            host.Sync(now);
            _lastSync = now;
        }
        host.PollHealth(now);
        host.ServiceActuations(now);   // release any line whose on_s has elapsed — every tick, never throttled
        RespondToWatchdog(now);
    }

    /// <summary>Sync every tick unless a controller cadence is in force, in which case only once it has
    /// elapsed. The first tick always syncs, so a freshly booted mound reports in immediately.</summary>
    private bool SyncDue(DateTimeOffset now) =>
        EffectiveSyncInterval is not { } interval || _lastSync is not { } last || now - last >= interval;

    /// <summary>
    /// Graceful shutdown: drive every actuator to its safe state, then persist authority. If a sticky
    /// trip is in force it is first escalated to a stop, so the halt survives the restart rather than
    /// being lost with the in-memory trip — otherwise this is NOT a stop, and a normal restart resumes
    /// the mound where it left off. Idempotent and safe to call more than once.
    /// </summary>
    public void Shutdown(DateTimeOffset now)
    {
        RespondToWatchdog(now);       // a live trip becomes a durable stop before we persist
        host.EnterSafeState();
        host.PersistAuthority();
    }

    /// <summary>
    /// The safe-state response, shared by tick and shutdown. A sticky trip becomes a persisted stop so
    /// it cannot be cleared by a reboot; any safe-state demand de-energizes the hardware now.
    /// </summary>
    private void RespondToWatchdog(DateTimeOffset now)
    {
        if (host.Guard.HasTrip)
        {
            host.Stop();               // Major.Stop() + de-energize — a stop a restart never clears
            host.PersistAuthority();   // make the halt durable before anything else
        }
        else if (host.Guard.SafeStateRequired)
        {
            host.EnterSafeState();     // a stale-heartbeat demand: de-energize, but do not permanently halt
        }
    }
}
