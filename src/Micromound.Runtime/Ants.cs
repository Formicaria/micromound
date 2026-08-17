using Micromound.Capabilities;
using Micromound.Protocol;

namespace Micromound.Runtime;

/// <summary>
/// The six default logical workers — ANTS.md. Names are deliberately distinct from any upstream
/// colony's own roles, so "Verifier" (upstream, judges missions) and "Witness Ant" (on-device,
/// judges whether a relay actually closed) never get confused for one another.
///
/// An ant is a specialized worker, NOT a language model instance. Most are deterministic code;
/// on a constrained controller several compile into one firmware image and remain ants only in
/// the metadata a UI renders.
/// </summary>
public static class DefaultAnts
{
    public const string MoundMajor = "Mound Major";
    public const string Scout = "Scout Ant";
    public const string Forager = "Forager Ant";
    public const string Guard = "Guard Ant";
    public const string Witness = "Witness Ant";
    public const string Cache = "Cache Ant";
    public const string Runner = "Runner Ant";

    public static readonly IReadOnlyList<string> All =
        [MoundMajor, Scout, Forager, Guard, Witness, Cache, Runner];
}

/// <summary>What a worker is, as the registry and any upstream UI see it.</summary>
public sealed class WorkerDescriptor
{
    public required string Name { get; init; }
    public string Purpose { get; init; } = "";
    /// <summary>deterministic | algorithmic | sensor | actuator | reasoning.</summary>
    public string RuntimeType { get; init; } = "deterministic";
    public IReadOnlyList<string> Consumes { get; init; } = [];
    public IReadOnlyList<string> Exposes { get; init; } = [];
    /// <summary>The worker's own ceiling, passed to the kernel on every request it makes.</summary>
    public ActionClass Ceiling { get; init; } = ActionClass.Observe;
    public string OfflineBehaviour { get; init; } = OfflineBehaviours.Continue;
    public bool RequiresReasoning { get; init; }
}

/// <summary>Health as a Guard Ant reports it, and as the Mound Major dispatches on.</summary>
public enum WorkerState
{
    Idle,
    Active,
    Degraded,
    Stopped
}

/// <summary>
/// Any local worker. Deliberately narrow: a worker is given a step and returns a result. It is
/// not given the kernel's executors, a driver handle, or a transport — the things that would let
/// it act outside the boundary are not in its reach, rather than merely discouraged.
/// </summary>
public interface IMoundWorker
{
    WorkerDescriptor Descriptor { get; }
    WorkerState State { get; }
}

/// <summary>Observation and sensing. Readings are evidence, and are returned as such.</summary>
public interface IScoutAnt : IMoundWorker
{
    /// <summary>Read a capability. The reading is returned as an evidence item, not a bare number.</summary>
    MissionStepResult Sense(string capability, DateTimeOffset now);
}

/// <summary>
/// Requested physical action. It translates a worker-level request into a capability request and
/// submits it to the kernel. It holds no driver and touches no hardware.
/// </summary>
public interface IForagerAnt : IMoundWorker
{
    ActionRecord Request(CapabilityRequest request, DateTimeOffset now);
}

/// <summary>
/// Runtime health and operational safety observation. It reports independent safety trips as
/// facts; it never resets, suppresses, or manages them (SAFETY.md Layer 0).
/// </summary>
public interface IGuardAnt : IMoundWorker
{
    /// <summary>Heartbeat, watchdog, power, thermal, connectivity, interlock status, faults.</summary>
    IReadOnlyList<EvidenceItem> Poll(DateTimeOffset now);

    /// <summary>True when the runtime should drop actuation and enter the declared safe state.</summary>
    bool SafeStateRequired { get; }
}

/// <summary>
/// Physical outcome confirmation — evidence gathered independently of the actuation path.
/// Distinct from any upstream Verifier: this one asks whether the valve actually opened.
/// </summary>
public interface IWitnessAnt : IMoundWorker
{
    /// <summary>Correlate an action with evidence and return the outcome it is entitled to.</summary>
    string Confirm(ActionRecord record, EvidencePolicy policy, DateTimeOffset now, out string reason);
}

/// <summary>
/// Short-term operational persistence: authority, mission, worker state, recent observations,
/// pending evidence, the durable outbound queue, the last acknowledged sequence.
///
/// Not a long-term memory system. Anything a mound could rebuild from its charter and its
/// hardware does not belong here.
/// </summary>
public interface ICacheAnt : IMoundWorker
{
    void Save<T>(string key, T value);
    bool TryLoad<T>(string key, out T value);
    void Delete(string key);
}

/// <summary>
/// Communication with the upstream controller: enrollment, signed sync beats, durable retry,
/// reconnect, backlog upload, charter and stop receipt. The only outward-facing worker.
/// </summary>
public interface IRunnerAnt : IMoundWorker
{
    bool IsConnected { get; }
    DateTimeOffset? LastSyncAt { get; }
    void Enqueue(Envelope envelope);
}
