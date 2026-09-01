using Micromound.Capabilities;
using Micromound.Protocol;

namespace Micromound.Evidence;

/// <summary>
/// The local evidence store — ARCHITECTURE.md Layer 6, the Witness Ant's memory.
///
/// Retention is a ring buffer sized by the hardware profile, with two ordered rules under
/// pressure. First: acknowledged proof — already delivered — is reclaimed oldest-first past the
/// soft capacity, reported as <c>evicted_acked_items</c>. Then, and only then: unacknowledged
/// proof is retained past the soft capacity because silently dropping it is indistinguishable from
/// never capturing it — but it is not unbounded. Past a hard ceiling the oldest unacknowledged
/// item is dropped and counted as <c>spilled_unacked_items</c>, so a mound offline for a week
/// bounds its storage and reports exactly how much proof the gap cost. Both counts ride the wire;
/// neither loss is ever silent.
/// </summary>
public interface IEvidenceStore : IEvidenceLookup
{
    void Add(EvidenceItem item);

    /// <summary>Items not yet acknowledged by the controller, oldest first.</summary>
    IReadOnlyList<EvidenceItem> Pending();

    /// <summary>Mark items acknowledged, making them eligible for eviction under pressure.</summary>
    void Acknowledge(IEnumerable<string> evidenceIds);

    /// <summary>How many acknowledged items storage pressure has forced out since the last bundle.</summary>
    int TakeEvictedCount();

    /// <summary>
    /// How many UNacknowledged items the hard ceiling forced out since the last bundle — proof the
    /// controller never saw. Reported so a spill is loud, never a silent gap in the audit trail.
    /// </summary>
    int TakeSpilledCount();
}

/// <summary>
/// Correlates actions with the evidence that does or does not support them — the Witness Ant's
/// actual job. The verdict rule itself is <see cref="EvidenceGate"/> in Micromound.Protocol; this
/// is what feeds it: locating prior readings, pairing a "before" with an "after", and deciding
/// which items a given action's claim actually rests on.
/// </summary>
public interface IEvidenceCorrelator
{
    /// <summary>
    /// Evidence relevant to an action: what the executor produced, plus anything the store holds
    /// that was captured close enough in time to bear on it (a soil reading taken before the
    /// valve opened is what makes the reading taken after it mean anything).
    /// </summary>
    IReadOnlyDictionary<string, EvidenceItem> For(ActionRecord record, EvidencePolicy policy, DateTimeOffset now);
}
