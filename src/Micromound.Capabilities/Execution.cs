using Micromound.Protocol;

namespace Micromound.Capabilities;

/// <summary>
/// One authorized actuation, handed to a driver. Everything here has already passed the kernel:
/// the parameters are effective values, not requested ones, and the limits are the intersected
/// bound the driver may assume it is inside.
/// </summary>
public sealed class CapabilityExecution
{
    public required string CapabilityId { get; init; }
    /// <summary>Set when this execution is a routine invocation rather than a single capability.</summary>
    public string RoutineId { get; init; } = "";
    /// <summary>Effective parameters — already clamped by every limit tier.</summary>
    public required IReadOnlyDictionary<string, double> Parameters { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    /// <summary>The intersected bound, for a driver that needs to know its own headroom.</summary>
    public required CapabilityLimits EffectiveLimits { get; init; }
    public string MissionId { get; init; } = "";
}

/// <summary>What a driver reports back. Never a bare bool — a fault has to say what faulted.</summary>
public sealed class ExecutionOutcome
{
    public required bool Succeeded { get; init; }
    public string Detail { get; init; } = "";
    /// <summary>
    /// Evidence captured during execution. For a <c>sense.</c> capability this is where the
    /// reading goes: the reading IS the evidence, and a sense executor that returns none will
    /// see its own result gated down to `unverified`.
    /// </summary>
    public IReadOnlyList<EvidenceItem> Evidence { get; init; } = [];
    /// <summary>When the work actually finished. Null ⇒ the kernel infers it from the duration parameter.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    public static ExecutionOutcome Ok(IReadOnlyList<EvidenceItem>? evidence = null,
        DateTimeOffset? endedAt = null, string detail = "") =>
        new() { Succeeded = true, Evidence = evidence ?? [], EndedAt = endedAt, Detail = detail };

    public static ExecutionOutcome Fault(string detail) =>
        new() { Succeeded = false, Detail = detail };
}

/// <summary>
/// The seam between the kernel and hardware — ARCHITECTURE.md Layer 4. Implementations live in
/// Micromound.Drivers (real hardware) and Micromound.Sim (fake hardware).
///
/// An executor is reached ONLY through the kernel. It is never handed to a worker, a workflow, or
/// a reasoning provider, because holding one is equivalent to holding the hardware: by the time
/// <see cref="Execute"/> is called every authority question has already been answered, and an
/// executor asked directly would answer none of them.
/// </summary>
public interface ICapabilityExecutor
{
    /// <summary>The capability or routine id this executor performs.</summary>
    string CapabilityId { get; }

    /// <summary>
    /// Driver health. False makes the kernel refuse with
    /// <see cref="RefusalReason.CapabilityUnavailable"/> rather than attempting the work.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Perform the work. Implementations should return <see cref="ExecutionOutcome.Fault"/> for
    /// an expected failure; a thrown exception is caught by the kernel and reported as a driver
    /// fault, which is a backstop rather than a supported style.
    /// </summary>
    ExecutionOutcome Execute(CapabilityExecution execution);
}

/// <summary>
/// Resolves evidence ids the kernel did not itself receive — prior readings a Witness Ant
/// captured before the action (a "soil_before" window), or items already in the local store.
/// Implemented by Micromound.Evidence; the kernel only reads.
/// </summary>
public interface IEvidenceLookup
{
    bool TryGet(string evidenceId, out EvidenceItem item);
}
