using Micromound.Capabilities;
using Micromound.Protocol;

namespace Micromound.Runtime;

// The three ants a mission passes through while it runs — ANTS.md. Scout observes, Forager
// requests action, Guard decides whether the mound is fit to act at all.
//
// The other three (Witness, Cache, Runner) act on the *record* rather than on the mission, and
// land with evidence correlation and transport. The seam is deliberate: these three sit on the
// path between a mission step and the hardware, and that path is the one worth getting right
// first.
//
// None of them decides authority. Scout and Forager submit to the capability kernel and report
// what it said; Guard can only ever make the mound do *less*. There is exactly one thing in this
// system that says yes, and it is not in this file.

/// <summary>
/// Observation and sensing — ANTS.md. A Scout's reading *is* evidence, which is why it comes back
/// as an <see cref="ActionRecord"/> with evidence refs rather than as a bare number: a reading
/// nothing captured is a reading nobody can check, and the kernel's evidence gate will say so.
///
/// It does not check that the capability it was handed is in the `sense.` namespace. It submits
/// with its declared ceiling and lets the kernel refuse — one decider, and a refusal that names
/// the worker beats a silent no from a worker that decided for itself.
/// </summary>
public sealed class ScoutAnt(CapabilityKernel kernel, IEvidenceLookup? evidence = null,
    WorkerDescriptor? descriptor = null) : IScoutAnt
{
    public WorkerDescriptor Descriptor { get; } = descriptor ?? new WorkerDescriptor
    {
        Name = DefaultAnts.Scout,
        Purpose = "observation and sensing",
        RuntimeType = RuntimeTypes.Sensor,
        Ceiling = ActionClass.Observe
    };

    public WorkerState State { get; private set; } = WorkerState.Idle;

    /// <summary>How many requests this ant has submitted — worker telemetry for a colony view.</summary>
    public int Requests { get; private set; }

    public ActionRecord Sense(CapabilityRequest request, DateTimeOffset now)
    {
        Requests++;
        State = WorkerState.Active;
        try
        {
            return kernel.Execute(Bind(request, Descriptor), now, evidence);
        }
        finally
        {
            State = WorkerState.Idle;
        }
    }

    /// <summary>
    /// Stamp the worker's identity and ceiling onto the request. Done here rather than by the
    /// caller so that an ant cannot be handed someone else's ceiling — the ceiling that binds is
    /// always the one belonging to the worker that actually made the request.
    /// </summary>
    internal static CapabilityRequest Bind(CapabilityRequest request, WorkerDescriptor descriptor)
    {
        var bound = new CapabilityRequest
        {
            Capability = request.Capability,
            MissionId = request.MissionId,
            Worker = descriptor.Name,
            WorkerCeiling = descriptor.Ceiling
        };

        foreach (var (name, value) in request.Parameters) bound.Parameters[name] = value;
        return bound;
    }
}

/// <summary>
/// Requested physical action — ANTS.md. It translates a worker-level request into a capability
/// request and submits it. **It holds no driver and no executor**, and there is no field on it
/// through which one could be supplied: the constructor takes the kernel, and the kernel is the
/// only thing that owns executors.
/// </summary>
public sealed class ForagerAnt(CapabilityKernel kernel, IEvidenceLookup? evidence = null,
    WorkerDescriptor? descriptor = null) : IForagerAnt
{
    public WorkerDescriptor Descriptor { get; } = descriptor ?? new WorkerDescriptor
    {
        Name = DefaultAnts.Forager,
        Purpose = "requested physical action, within charter",
        RuntimeType = RuntimeTypes.Actuator,
        Ceiling = ActionClass.Benign
    };

    public WorkerState State { get; private set; } = WorkerState.Idle;

    /// <summary>How many requests this ant has submitted — worker telemetry for a colony view.</summary>
    public int Requests { get; private set; }

    public ActionRecord Request(CapabilityRequest request, DateTimeOffset now)
    {
        Requests++;
        State = WorkerState.Active;
        try
        {
            return kernel.Execute(ScoutAnt.Bind(request, Descriptor), now, evidence);
        }
        finally
        {
            State = WorkerState.Idle;
        }
    }
}

/// <summary>
/// Runtime health and operational safety — ANTS.md, and the implementation of the software
/// watchdog SAFETY.md Layer 1 has always promised: *loss of the runtime's own heartbeat drops
/// actuation and enters the declared safe state.* Until now that sentence existed only in the
/// document; <see cref="IGuardAnt.SafeStateRequired"/> was declared and read by nothing.
///
/// Two things make it demand a safe state, and they behave differently on purpose.
///
/// **A stale heartbeat** is self-healing. Something is supposed to call <see cref="Beat"/> on a
/// cadence; if it stops, the mound stops acting, and if it resumes, the heartbeat is fresh again.
/// A watchdog that latched on a scheduling hiccup would be a watchdog nobody left enabled.
///
/// **A reported trip is sticky, and there is no method here that clears one.** SAFETY.md Layer 0
/// is explicit: a Guard Ant reports an interlock trip, it does not clear one. Software that could
/// clear a trip is software that could be asked to clear a trip, and the way to guarantee that
/// never happens is to give it nowhere to enter. Recovery is an operator clearing the stop, which
/// returns the mound to observe-only and waits for a fresh charter.
/// </summary>
public sealed class GuardAnt : IGuardAnt
{
    private readonly Dictionary<string, string> _trips = new(StringComparer.Ordinal);
    private readonly Action<EvidenceItem>? _publish;
    private DateTimeOffset? _lastBeat;
    private bool _heartbeatStale;
    private long _polls;

    /// <param name="heartbeatTimeoutSeconds">
    /// How long a heartbeat stays fresh. Zero disables the heartbeat check entirely, for a
    /// deployment whose liveness is guaranteed some other way — an explicit choice, not a default.
    /// </param>
    /// <param name="publish">Receives the health evidence each poll produces, when a store exists.</param>
    public GuardAnt(double heartbeatTimeoutSeconds = 30, Action<EvidenceItem>? publish = null)
    {
        HeartbeatTimeoutSeconds = Math.Max(0, heartbeatTimeoutSeconds);
        _publish = publish;
    }

    public double HeartbeatTimeoutSeconds { get; }

    public WorkerDescriptor Descriptor { get; } = new()
    {
        Name = DefaultAnts.Guard,
        Purpose = "runtime health, watchdog, and observed safety trips",
        RuntimeType = RuntimeTypes.Deterministic,
        Ceiling = ActionClass.Observe
    };

    public WorkerState State => SafeStateRequired ? WorkerState.Degraded : WorkerState.Idle;

    /// <summary>True when the mound must not actuate. Reflects the most recent <see cref="Poll"/>.</summary>
    public bool SafeStateRequired => _heartbeatStale || _trips.Count > 0;

    /// <summary>Why, in words, for the record that refuses the work. Empty when nothing is wrong.</summary>
    public string Reason =>
        _trips.Count > 0
            ? "safety trip observed — " + string.Join("; ", _trips.Select(t => $"{t.Key}: {t.Value}"))
            : _heartbeatStale
                ? $"runtime heartbeat stale (timeout {HeartbeatTimeoutSeconds}s)"
                : "";

    /// <summary>The runtime is alive. Called on a cadence by whatever owns the loop.</summary>
    public void Beat(DateTimeOffset now) => _lastBeat = now;

    /// <summary>
    /// Record an observed safety trip — an interlock, a thermal cut-out, a limit switch. Sticky
    /// by construction: nothing here clears it.
    /// </summary>
    public void ReportTrip(string source, string detail) => _trips[source] = detail;

    public IReadOnlyList<EvidenceItem> Poll(DateTimeOffset now)
    {
        _heartbeatStale = HeartbeatTimeoutSeconds > 0 &&
                          (_lastBeat is not { } beat || (now - beat).TotalSeconds > HeartbeatTimeoutSeconds);

        // Health is reported as evidence rather than as a log line, because a mound that entered
        // its safe state has to be able to prove afterwards why it did — SAFETY.md forbids silent
        // anything, and "it just stopped" is the silent kind.
        var age = _lastBeat is { } last ? (now - last).TotalSeconds : -1;

        // The counter, not the clock, makes the id unique. Two polls inside the same wire-format
        // second are ordinary, and an evidence store keyed by id would silently keep one of them.
        var item = EvidenceReadings.Create(
            $"guard-{++_polls}-{now.ToWire()}", "sense.runtime_heartbeat_age_s", age, now,
            unit: "seconds", source: DefaultAnts.Guard);

        _publish?.Invoke(item);
        return [item];
    }
}
