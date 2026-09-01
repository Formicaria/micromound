using Micromound.Capabilities;
using Micromound.Protocol;

namespace Micromound.Runtime;

/// <summary>
/// Registry of the workers this mound is running — populated from the manifest at startup and
/// surfaced to any upstream UI that draws the colony.
/// </summary>
public sealed class WorkerRegistry
{
    private readonly Dictionary<string, IMoundWorker> _workers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => _workers.Keys;

    public IEnumerable<IMoundWorker> All => _workers.Values;

    public void Register(IMoundWorker worker) => _workers[worker.Descriptor.Name] = worker;

    public bool TryGet(string name, out IMoundWorker worker) => _workers.TryGetValue(name, out worker!);

    /// <summary>Workers that can supply a capability — how the Mound Major resolves a step.</summary>
    public IEnumerable<IMoundWorker> Providing(string capability) =>
        _workers.Values.Where(w => w.Descriptor.Consumes.Contains(capability)
                                   || w.Descriptor.Exposes.Contains(capability));
}

/// <summary>
/// The local coordinator — ARCHITECTURE.md Layer 2. Replaces the earlier "Edge Queen" name.
///
/// It is a workflow and state-machine coordinator, not an always-running agent. It accepts
/// bounded authority, walks a mission's ordered steps, evaluates deterministic conditions,
/// dispatches to local ants, and produces a structured outcome. It asks for reasoning only when
/// the manifest configures it and a step genuinely needs it, and what comes back is a proposal
/// that still has to survive the kernel.
///
/// M1 deliverable — see docs/ROADMAP.md. The interface is settled here because the mission
/// contract, the kernel, and the six ants it coordinates all exist already; what is missing is
/// the loop between them.
/// </summary>
public interface IMoundMajor
{
    string MoundId { get; }

    /// <summary>Current authority state: stopped, observe_only, quiesced, or chartered.</summary>
    string State { get; }

    WorkerRegistry Workers { get; }

    /// <summary>Offer a charter. Invalid charters are refused and reported; state is untouched.</summary>
    ValidationResult AcceptCharter(Charter charter, DateTimeOffset now);

    /// <summary>Apply a signed configuration manifest. Fails closed, keeping the previous one.</summary>
    ValidationResult ApplyManifest(MoundManifest manifest, DateTimeOffset now);

    /// <summary>
    /// Validate and execute a structured work packet. A mission that references anything outside
    /// its charter is refused whole rather than partially run.
    /// </summary>
    MissionReport Execute(Mission mission, DateTimeOffset now);

    /// <summary>
    /// The controller acknowledged a sync beat — the ONE renewal path PROTOCOL.md §5 allows, and
    /// on this interface precisely so the Runner Ant, which is the component that hears the
    /// acknowledgement, can report it. Renewal never revives a quiesced or stopped mound.
    /// </summary>
    void RenewLease(DateTimeOffset now);

    /// <summary>Cease actuation, enter the declared safe state, keep sensing and syncing.</summary>
    void Stop();

    /// <summary>
    /// Clear the durable in-flight mission checkpoint. Called by whoever publishes a mission's
    /// terminal report, immediately AFTER that report is durably queued — so on a durable store a
    /// crash between the two re-reports the mission on the next restart rather than losing the
    /// record. A no-op when no checkpoint is present. Never called mid-mission.
    /// </summary>
    void ClearMissionCheckpoint();
}

/// <summary>
/// The default workflow every mission is expressed in — ARCHITECTURE.md "Default workflow".
///
///     SENSE → EVALUATE → ACT → SENSE AGAIN → VERIFY → REPORT
///
/// The second sense is not redundancy. It is the entire reason the mound can claim anything
/// happened: the first reading justifies the action, the second is independent evidence of its
/// effect, and without it the outcome is `unverified` no matter what the driver returned.
/// </summary>
public static class DefaultWorkflow
{
    public static readonly IReadOnlyList<string> Phases =
        ["sense", "evaluate", "act", "sense_again", "verify", "report"];
}
