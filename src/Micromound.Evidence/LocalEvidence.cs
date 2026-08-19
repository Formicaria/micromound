using Micromound.Capabilities;
using Micromound.Protocol;

namespace Micromound.Evidence;

/// <summary>
/// The local evidence store, in memory — ARCHITECTURE.md Layer 6.
///
/// Retention has one rule that overrides capacity: **evidence pending synchronization is never
/// evicted before it is acknowledged.** Under pressure the oldest *acknowledged* items go first,
/// and how many went is reported on the wire as <c>evicted_acked_items</c>, because silently
/// dropping proof is indistinguishable from never having captured it.
///
/// When capacity is exhausted and nothing is acknowledged, this store **keeps growing** rather
/// than dropping unacknowledged proof. That is the correct trade for the two of them, and it is
/// also not a complete answer: bounding a device that has been offline for a week is the Cache
/// Ant's problem, and the Cache Ant is durable storage rather than a dictionary. Recorded here so
/// the limitation is a decision rather than an oversight.
/// </summary>
public sealed class InMemoryEvidenceStore(int capacity = 2000) : IEvidenceStore
{
    private readonly Dictionary<string, EvidenceItem> _items = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);
    private int _evicted;

    public int Capacity { get; } = Math.Max(1, capacity);

    public int Count => _items.Count;

    public void Add(EvidenceItem item)
    {
        if (string.IsNullOrEmpty(item.EvidenceId)) return;

        if (!_items.ContainsKey(item.EvidenceId)) _order.Add(item.EvidenceId);
        _items[item.EvidenceId] = item;

        Evict();
    }

    public bool TryGet(string evidenceId, out EvidenceItem item)
    {
        if (_items.TryGetValue(evidenceId, out var found))
        {
            item = found;
            return true;
        }

        item = new EvidenceItem();
        return false;
    }

    public IReadOnlyList<EvidenceItem> Pending() =>
        _order.Where(id => !_acknowledged.Contains(id)).Select(id => _items[id]).ToList();

    public void Acknowledge(IEnumerable<string> evidenceIds)
    {
        foreach (var id in evidenceIds)
            if (_items.ContainsKey(id))
                _acknowledged.Add(id);

        // Acknowledgement is what makes eviction possible, so it is also when eviction happens.
        Evict();
    }

    /// <summary>Reads and resets the count. It rides out on the next bundle and is not repeated.</summary>
    public int TakeEvictedCount()
    {
        var count = _evicted;
        _evicted = 0;
        return count;
    }

    private void Evict()
    {
        while (_items.Count > Capacity)
        {
            var victim = _order.FirstOrDefault(_acknowledged.Contains);
            if (victim is null) return;   // nothing acknowledged: keep the proof, exceed the bound

            _order.Remove(victim);
            _acknowledged.Remove(victim);
            _items.Remove(victim);
            _evicted++;
        }
    }
}

/// <summary>
/// Resolves the evidence an action's claim rests on — the lookup half of the Witness Ant's job.
///
/// It resolves the refs the record actually carries, and nothing else. It is deliberately not
/// cleverer than that: a correlator that swept up every reading captured near an action would let
/// the mound nominate its own corroboration, and "commands are not evidence" means very little if
/// a device may decide after the fact which observations happen to support it.
///
/// Evidence becomes an action's evidence in exactly two ways: the executor produced it during the
/// work, or a mission explicitly linked it with a `verify` step's <c>confirms</c>. Both are
/// somebody else's decision, made before the outcome was known.
/// </summary>
public sealed class EvidenceCorrelator(IEvidenceLookup store) : IEvidenceCorrelator
{
    public IReadOnlyDictionary<string, EvidenceItem> For(ActionRecord record, EvidencePolicy policy,
        DateTimeOffset now)
    {
        var view = new Dictionary<string, EvidenceItem>(StringComparer.Ordinal);

        foreach (var id in record.EvidenceRefs)
            if (!view.ContainsKey(id) && store.TryGet(id, out var item))
                view[id] = item;

        // A ref that does not resolve is deliberately absent rather than stubbed. The evidence
        // gate treats a missing item as unverified, which is the honest reading of "this action
        // cites proof nobody can produce".
        return view;
    }
}
