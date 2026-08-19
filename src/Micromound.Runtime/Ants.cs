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
    /// <summary>
    /// Read a capability. Deliberately the same shape as <see cref="IForagerAnt.Request"/> and
    /// returning the same <see cref="ActionRecord"/>: a reading is an action the mound took and
    /// has to account for, its evidence refs are what make it checkable, and one shape means the
    /// coordinator has one place that turns a record into a step result rather than two.
    /// </summary>
    ActionRecord Sense(CapabilityRequest request, DateTimeOffset now);
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

    /// <summary>
    /// Why, in words, for the record that refuses the work. Part of the interface rather than an
    /// implementation detail because SAFETY.md forbids silent anything: "a refusal without a
    /// reason is itself a contract violation". Empty when nothing is wrong.
    /// </summary>
    string Reason { get; }
}

/// <summary>
/// Physical outcome confirmation — evidence gathered independently of the actuation path.
/// Distinct from any upstream Verifier: this one asks whether the valve actually opened.
/// </summary>
public interface IWitnessAnt : IMoundWorker
{
    /// <summary>
    /// Correlate an action with the observation offered as proof of its effect, and return the
    /// outcome the action is entitled to.
    ///
    /// <paramref name="confirming"/> is an argument rather than something the Witness goes looking
    /// for. Evidence becomes an action's evidence in exactly two ways — an executor produced it
    /// during the work, or a mission explicitly linked it with a `verify` step's <c>confirms</c> —
    /// and both are somebody else's decision, made before the outcome was known. A Witness that
    /// swept up nearby readings itself would let the mound nominate its own corroboration.
    ///
    /// It can only ever lower the verdict. That is a property of the evidence gate rather than a
    /// rule this implementation applies: the gate returns the record's own outcome unless that
    /// outcome asserts physical work, so nothing here can talk an `unverified` action back into
    /// having succeeded.
    /// </summary>
    string Confirm(ActionRecord record, IReadOnlyList<EvidenceItem> confirming, EvidencePolicy policy,
        DateTimeOffset now, out string reason);
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
