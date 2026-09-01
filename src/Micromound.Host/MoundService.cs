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
    public MoundHost Host => host;

    /// <summary>True when the watchdog demands safe state — a sticky safety trip or a stale heartbeat.</summary>
    public bool SafeStateEngaged => host.Guard.SafeStateRequired;

    /// <summary>
    /// One service tick: mark the runtime alive, run a sync beat, refresh the watchdog, and respond
    /// to it. A sticky trip is escalated to a persisted stop (durable across a restart); any
    /// safe-state demand also physically de-energizes now. Idempotent.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        host.Beat(now);
        host.Sync(now);
        host.PollHealth(now);
        RespondToWatchdog(now);
    }

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
