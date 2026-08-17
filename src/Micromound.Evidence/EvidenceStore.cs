using Micromound.Capabilities;
using Micromound.Protocol;

namespace Micromound.Evidence;

/// <summary>
/// The local evidence store — ARCHITECTURE.md Layer 6, the Witness Ant's memory.
///
/// Retention is a ring buffer sized by the hardware profile, with one rule that overrides
/// capacity: evidence pending synchronization is never evicted before it is acknowledged, unless
/// storage exhaustion forces oldest-acked-first eviction — and that eviction is itself reported
/// on the wire via <c>evicted_acked_items</c>. Silently dropping proof is indistinguishable from
/// never having captured it.
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

/// <summary>
/// The durable uplink queue as evidence sees it: bundles built from pending items, hash-chained
/// through envelope <c>prev_digest</c> so offline gaps and reordering are detectable after the
/// fact rather than merely suspected.
/// </summary>
public interface IEvidenceBundler
{
    /// <summary>Build the next bundle from pending items, oldest first, up to a size budget.</summary>
    EvidenceBundle NextBundle(int maxItems, DateTimeOffset now);
}
