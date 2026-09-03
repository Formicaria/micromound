using System.Globalization;
using System.Text.Json;
using Micromound.Evidence;
using Micromound.Protocol;

namespace Micromound.Sync;

/// <summary>
/// The durable evidence store the M4 host runs on — the Witness Ant's memory, on disk. It is the
/// <see cref="InMemoryEvidenceStore"/>'s exact retention policy (ARCHITECTURE.md Layer 6, proven at
/// <c>v0.9.0</c>) with a file behind every item, so the proof a mound has captured survives a process
/// or device restart instead of evaporating with the heap. The M3 retention *rules* do not change
/// here; only the substrate under them does — a directory of files, not a database.
///
/// <para><b>Shape on disk.</b> One file per item, named by its insertion sequence
/// (<c>0000000000000042.json</c>): the name IS the order, so the ring buffer's oldest-first eviction is
/// recoverable from a directory listing alone, and the item's id lives inside the JSON where it needs
/// no encoding. An acknowledged item has a sibling marker (<c>0000000000000042.ack</c>). A small
/// <c>counters.json</c> holds the evicted/spilled counts that have not yet ridden the wire, because a
/// spill the controller was never told about would be exactly the silent loss the policy exists to
/// prevent — and a restart must not be how it goes quiet.</para>
///
/// <para><b>Reads are served from memory; writes are written through.</b> The store keeps the same
/// in-memory mirror the in-memory store is made of and rebuilds it from the directory on open, so
/// <see cref="TryGet"/> and <see cref="Pending"/> cost nothing extra and the correlator is unchanged.
/// Every mutation goes to disk first through the shared durability primitives (<see cref="DurableFiles"/>:
/// temp-write, flush, rename, directory fsync) before the mirror reflects it.</para>
///
/// <para><b>Crash order, chosen so nothing is ever lost, only re-sent.</b> An item is written before it
/// is remembered, so a crash leaves either a whole item or none. An acknowledgement marker is written
/// after the item, so a crash between them leaves the item PENDING — re-sent, re-acknowledged,
/// harmless — never acknowledged-but-absent. An eviction unlinks the item BEFORE its marker, so a
/// crash between them leaves an orphan marker, which the next open ignores and sweeps; the reverse
/// order could leave an item that had been acknowledged looking pending, which is also harmless, but
/// this order is the one that never resurrects proof the policy already chose to drop. A file that
/// exists but will not parse is a fault reported loudly at open and skipped — not a reason to refuse
/// to start, and not silently treated as proof.</para>
/// </summary>
public sealed class FileEvidenceStore : IEvidenceStore
{
    private const string ItemSuffix = ".json";
    private const string AckSuffix = ".ack";
    private const string CountersFile = "counters.json";
    private const int SequenceWidth = 16;

    private readonly string _directory;
    private readonly string _tempDirectory;
    private readonly object _gate = new();

    // The mirror: the same structures the in-memory store is made of.
    private readonly Dictionary<string, EvidenceItem> _items = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly HashSet<string> _acknowledged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sequenceOf = new(StringComparer.Ordinal);
    private long _nextSequence;
    private int _evicted;
    private int _spilled;

    /// <summary>What went wrong while opening, if anything: unreadable item files that were skipped.
    /// Loud by design — a mound that lost proof to corruption should be able to say so.</summary>
    public IReadOnlyList<string> OpenFaults { get; }

    /// <param name="directory">Where the items live; created if needed.</param>
    /// <param name="capacity">Soft bound: acknowledged proof is reclaimed past this.</param>
    /// <param name="hardCeiling">Hard bound on total items; defaults to twice the soft capacity, never below it.</param>
    public FileEvidenceStore(string directory, int capacity = 2000, int? hardCeiling = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Capacity = Math.Max(1, capacity);
        HardCeiling = Math.Max(Capacity, hardCeiling ?? Capacity * 2);

        _directory = directory;
        _tempDirectory = Path.Combine(directory, DurableFiles.TempDirName);
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_tempDirectory);
        DurableFiles.SweepTemporaries(_tempDirectory);

        OpenFaults = Load();

        // A store reopened under a smaller configured bound (a lowered capacity, a lowered ceiling)
        // is brought back inside it now, with the reclaim counted like any other, rather than
        // running over its limits until the next Add happens to trigger eviction.
        Evict();
    }

    public int Capacity { get; }

    /// <summary>The absolute cap on stored items; past it, unacknowledged proof spills oldest-first.</summary>
    public int HardCeiling { get; }

    public int Count { get { lock (_gate) return _items.Count; } }

    public void Add(EvidenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrEmpty(item.EvidenceId)) return;

        lock (_gate)
        {
            // A re-add of a known id replaces the item in place — same file, same position in the
            // order — exactly as the in-memory store overwrites without re-appending.
            var isNew = !_sequenceOf.TryGetValue(item.EvidenceId, out var sequence);
            if (isNew)
                sequence = _nextSequence++;

            // Disk first: the item exists durably before the mirror admits it.
            DurableFiles.WriteAtomic(_tempDirectory, ItemPath(sequence), JsonSerializer.Serialize(item, ProtocolJson.Options));

            if (isNew)
            {
                _order.Add(item.EvidenceId);
                _sequenceOf[item.EvidenceId] = sequence;
            }
            _items[item.EvidenceId] = item;

            Evict();
        }
    }

    public bool TryGet(string evidenceId, out EvidenceItem item)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(evidenceId, out var found))
            {
                item = found;
                return true;
            }
        }
        item = new EvidenceItem();
        return false;
    }

    public IReadOnlyList<EvidenceItem> Pending()
    {
        lock (_gate)
            return _order.Where(id => !_acknowledged.Contains(id)).Select(id => _items[id]).ToList();
    }

    public void Acknowledge(IEnumerable<string> evidenceIds)
    {
        ArgumentNullException.ThrowIfNull(evidenceIds);
        lock (_gate)
        {
            foreach (var id in evidenceIds)
            {
                if (!_sequenceOf.TryGetValue(id, out var sequence) || _acknowledged.Contains(id))
                    continue;
                // The marker is written AFTER the item already exists, so a crash here leaves the item
                // pending — re-sent and re-acknowledged, never acknowledged-and-gone.
                DurableFiles.WriteAtomic(_tempDirectory, AckPath(sequence), "");
                _acknowledged.Add(id);
            }

            // Acknowledgement is what makes eviction possible, so it is also when eviction happens.
            Evict();
        }
    }

    /// <summary>Reads and resets the count. It rides out on the next bundle and is not repeated.</summary>
    public int TakeEvictedCount()
    {
        lock (_gate)
        {
            var count = _evicted;
            _evicted = 0;
            if (count != 0) PersistCounters();
            return count;
        }
    }

    /// <summary>Reads and resets the spill count. Rides the next bundle once, like the evicted count.</summary>
    public int TakeSpilledCount()
    {
        lock (_gate)
        {
            var count = _spilled;
            _spilled = 0;
            if (count != 0) PersistCounters();
            return count;
        }
    }

    // ---- retention: the in-memory store's policy, verbatim, with the disk kept in step ----------

    private void Evict()
    {
        var changed = false;

        // First reclaim from acknowledged proof — the controller already has it, so dropping it
        // costs the audit trail nothing (the count still rides the wire).
        while (_items.Count > Capacity)
        {
            var victim = _order.FirstOrDefault(_acknowledged.Contains);
            if (victim is null) break;   // nothing acknowledged left to reclaim

            Remove(victim);
            _evicted++;
            changed = true;
        }

        // Unacknowledged proof is allowed past the soft capacity — silently dropping it would be
        // indistinguishable from never capturing it — but not past the hard ceiling. Beyond it the
        // oldest unacknowledged item spills, and every spill is counted so the loss is loud.
        while (_items.Count > HardCeiling)
        {
            Remove(_order[0]);   // the oldest, and by construction now unacknowledged
            _spilled++;
            changed = true;
        }

        if (changed)
            PersistCounters();   // a count the controller has not yet seen must survive a restart
    }

    private void Remove(string id)
    {
        var sequence = _sequenceOf[id];
        // Item before marker: a crash between them leaves an orphan marker (ignored on open), never a
        // resurrected item the policy had already chosen to drop.
        DurableFiles.Delete(ItemPath(sequence));
        DurableFiles.Delete(AckPath(sequence));

        _order.Remove(id);
        _acknowledged.Remove(id);
        _items.Remove(id);
        _sequenceOf.Remove(id);
    }

    // ---- disk layout -----------------------------------------------------------------------------

    private string ItemPath(long sequence) => Path.Combine(_directory, sequence.ToString($"D{SequenceWidth}", CultureInfo.InvariantCulture) + ItemSuffix);
    private string AckPath(long sequence) => Path.Combine(_directory, sequence.ToString($"D{SequenceWidth}", CultureInfo.InvariantCulture) + AckSuffix);
    private string CountersPath => Path.Combine(_directory, CountersFile);

    /// <summary>Rebuild the mirror from the directory. Returns the faults met on the way, loud not fatal.</summary>
    private List<string> Load()
    {
        var faults = new List<string>();
        var loaded = new List<(long sequence, EvidenceItem item)>();
        var acked = new HashSet<long>();

        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(path);
            if (name == CountersFile) continue;

            if (name.EndsWith(AckSuffix, StringComparison.Ordinal))
            {
                if (TryParseSequence(name[..^AckSuffix.Length], out var ackSeq)) acked.Add(ackSeq);
                continue;
            }
            if (!name.EndsWith(ItemSuffix, StringComparison.Ordinal) || !TryParseSequence(name[..^ItemSuffix.Length], out var sequence))
                continue;   // not ours; leave it alone

            try
            {
                var item = JsonSerializer.Deserialize<EvidenceItem>(File.ReadAllText(path), ProtocolJson.Options);
                if (item is null || string.IsNullOrEmpty(item.EvidenceId))
                {
                    faults.Add($"{name}: no evidence_id; skipped");
                    continue;
                }
                loaded.Add((sequence, item));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Corrupt proof is a fault to report, not a reason to refuse to start and not proof.
                faults.Add($"{name}: unreadable ({ex.GetType().Name}); skipped");
            }
        }

        foreach (var (sequence, item) in loaded.OrderBy(e => e.sequence))
        {
            if (_sequenceOf.ContainsKey(item.EvidenceId))
            {
                // Two files claim one id — a re-add that crashed mid-way, or tampering. Keep the later
                // sequence (the newest write) and drop the older file so the store is consistent.
                faults.Add($"{item.EvidenceId}: duplicate on disk; kept the newest");
                var stale = _sequenceOf[item.EvidenceId];
                _order.Remove(item.EvidenceId);
                _acknowledged.Remove(item.EvidenceId);
                TryDelete(ItemPath(stale)); TryDelete(AckPath(stale));
            }
            _order.Add(item.EvidenceId);
            _items[item.EvidenceId] = item;
            _sequenceOf[item.EvidenceId] = sequence;
            if (acked.Contains(sequence)) _acknowledged.Add(item.EvidenceId);
            _nextSequence = Math.Max(_nextSequence, sequence + 1);
        }

        // Orphan markers — an eviction that crashed between unlinking the item and its marker — refer
        // to nothing and are swept so they cannot accumulate.
        foreach (var orphan in acked.Where(seq => !_sequenceOf.ContainsValue(seq)))
            TryDelete(AckPath(orphan));

        LoadCounters();
        return faults;
    }

    private static bool TryParseSequence(string text, out long sequence) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out sequence) && text.Length == SequenceWidth;

    private sealed record Counters(int Evicted, int Spilled);

    private void PersistCounters()
    {
        try { DurableFiles.WriteAtomic(_tempDirectory, CountersPath, JsonSerializer.Serialize(new Counters(_evicted, _spilled))); }
        catch (IOException) { /* the counts stay in memory for this run; losing them costs one report line, not proof */ }
    }

    private void LoadCounters()
    {
        try
        {
            if (!File.Exists(CountersPath)) return;
            var counters = JsonSerializer.Deserialize<Counters>(File.ReadAllText(CountersPath));
            _evicted = Math.Max(0, counters?.Evicted ?? 0);
            _spilled = Math.Max(0, counters?.Spilled ?? 0);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt counters file costs, at most, one report of counts already lost — not proof.
        }
    }

    private static void TryDelete(string path)
    {
        try { DurableFiles.Delete(path); } catch (IOException) { /* best effort at open */ }
    }
}
