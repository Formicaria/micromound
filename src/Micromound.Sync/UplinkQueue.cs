using System.Text.Json;
using System.Text.Json.Serialization;
using Micromound.Protocol;

namespace Micromound.Sync;

/// <summary>
/// The durable uplink queue — PROTOCOL.md §1's "uplink envelopes queue durably on-device and
/// drain oldest-first on reconnect", as a component.
///
/// The queue owns the chain. <see cref="NextSeq"/> and <see cref="LastDigest"/> are its state,
/// and <see cref="Enqueue"/> REFUSES an envelope that does not continue them — by throwing,
/// because a forked or reordered uplink chain is a programming error on this device, not wire
/// input to be tolerated. PROTOCOL.md §6 makes gaps and reordering *detectable*; this class is
/// why they never have to be detected in the first place.
///
/// Two watermarks matter and they move independently:
///
/// - <see cref="LastDigest"/> advances on every enqueue and NEVER retreats — it anchors the next
///   envelope, whether or not earlier ones have been acknowledged or evicted.
/// - <see cref="AcknowledgedThroughSeq"/> advances on every ack and is what makes an envelope
///   eligible to leave the queue. Until it covers a sequence number, that envelope is retained
///   and will be re-sent — the controller deduplicates by sequence, so re-delivery is safe and
///   loss is not.
///
/// <para><b>Persisted as segments, and bounded</b> (`v0.9.34`, roadmap P0.7). Every mutation used to
/// reserialize the ENTIRE queue into one document, and the queue had no bound at all. Measured on
/// this repository's own store: 4,000 queued action records was a <b>2.4 MB state file rewritten in
/// full on every enqueue</b>, ~12 ms each — and it kept growing, because nothing stopped it. On a
/// Pi's SD card that is write amplification measured in gigabytes per day of outage, and the only
/// thing that ever ended it was the disk filling.</para>
///
/// <para>Now each envelope is its own small document keyed by sequence, so an enqueue writes one
/// segment and a tiny head; the head carries the watermarks and the loss counters. Acknowledgement
/// deletes the segments it covers. There is no index to enumerate: the head knows the range
/// (<c>acked_through+1 .. next_seq-1</c>) and restore walks it, treating a missing segment as a gap
/// rather than an error — which is exactly what a spill leaves behind, and the chain makes it
/// visible at the controller either way.</para>
///
/// <para>With no store supplied the queue is memory-only, which is the simulator's mode and an
/// explicit choice, never a fallback.</para>
/// </summary>
public sealed class DurableUplinkQueue : IUplinkQueue
{
    private const string StoreKey = "sync:uplink-queue";
    private const string SegmentPrefix = "sync:uplink/";

    /// <summary>
    /// How many unacknowledged envelopes the queue will hold before the oldest are spilled. A bound
    /// is not optional: without one, a mound offline long enough fills its disk, and a mound that
    /// cannot write cannot record what it did.
    /// </summary>
    public const int DefaultMaxPending = 5000;

    /// <summary>
    /// The byte bound, which is the one that actually protects a small device — envelope sizes vary
    /// by orders of magnitude between a beat and an action record carrying inline evidence, so an
    /// item count alone bounds the wrong quantity.
    /// </summary>
    public const long DefaultMaxPendingBytes = 8L * 1024 * 1024;

    private readonly IStateStore? _store;
    private readonly List<Envelope> _pending = [];
    private readonly Dictionary<long, int> _segmentBytes = [];
    private readonly int _maxPending;
    private readonly long _maxPendingBytes;
    private long _nextSeq;
    private string _lastDigest = "";
    private long _ackedThrough = -1;
    private long _pendingBytes;
    private int _spilled;

    public DurableUplinkQueue(IStateStore? store = null,
        int maxPending = DefaultMaxPending, long maxPendingBytes = DefaultMaxPendingBytes)
    {
        _store = store;
        _maxPending = maxPending > 0 ? maxPending : DefaultMaxPending;
        _maxPendingBytes = maxPendingBytes > 0 ? maxPendingBytes : DefaultMaxPendingBytes;

        if (_store is not null && _store.TryGet(StoreKey, out var saved))
            RestoreFrom(saved);
    }

    /// <summary>
    /// Unacknowledged envelopes the queue had to drop under pressure — records the controller will
    /// never see, and a gap in a signed chain. Counted so it can ride the next beat and be answered
    /// for; SAFETY.md forbids losing something quietly, and a chain gap the controller can detect
    /// but not explain is halfway to silent. Read-and-clear, like the evidence store's counters.
    /// </summary>
    public int TakeSpilledCount()
    {
        var spilled = _spilled;
        _spilled = 0;
        PersistHead();
        return spilled;
    }

    /// <summary>Bytes of unacknowledged envelopes currently retained. For a health view and the budgets doc.</summary>
    public long PendingBytes => _pendingBytes;

    public long NextSeq => _nextSeq;

    public string LastDigest => _lastDigest;

    public long AcknowledgedThroughSeq => _ackedThrough;

    public int Depth => _pending.Count;

    public void Enqueue(Envelope envelope)
    {
        // The chain is checked, not trusted, even from our own runtime. An envelope that skips a
        // sequence number or anchors to the wrong digest would make the whole backlog unverifiable
        // at the controller — better to fail here, loudly, on the device that made the mistake.
        if (envelope.Seq != _nextSeq)
            throw new InvalidOperationException(
                $"uplink chain violation: envelope seq {envelope.Seq}, queue expects {_nextSeq}");

        if (!string.Equals(envelope.PrevDigest, _lastDigest, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"uplink chain violation: envelope prev_digest '{envelope.PrevDigest}', queue chain head is '{_lastDigest}'");

        if (string.IsNullOrEmpty(envelope.Signature))
            throw new InvalidOperationException(
                "uplink chain violation: unsigned envelope; there is no unsigned mode");

        _pending.Add(envelope);
        _nextSeq = envelope.Seq + 1;
        _lastDigest = envelope.Digest();

        var segment = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        _segmentBytes[envelope.Seq] = segment.Length;
        _pendingBytes += segment.Length;
        _store?.Put(SegmentKey(envelope.Seq), segment);

        Trim();
        PersistHead();
    }

    /// <summary>
    /// Enforce the bounds, oldest-first. The chain head does NOT retreat when an envelope is
    /// spilled — <see cref="LastDigest"/> and <see cref="NextSeq"/> keep advancing — so what the
    /// controller receives after a spill still verifies as a chain, with a visible gap where the
    /// dropped sequence numbers were. That is the design: PROTOCOL.md §6 makes loss DETECTABLE
    /// rather than deniable, and the counter alongside it makes it explicable.
    /// </summary>
    private void Trim()
    {
        while (_pending.Count > _maxPending || _pendingBytes > _maxPendingBytes)
        {
            if (_pending.Count <= 1) return;   // never spill the only thing we have left to say

            var oldest = _pending[0];
            _pending.RemoveAt(0);
            DropSegment(oldest.Seq);
            _spilled++;
        }
    }

    private void DropSegment(long seq)
    {
        if (_segmentBytes.Remove(seq, out var bytes)) _pendingBytes -= bytes;
        _store?.Delete(SegmentKey(seq));
    }

    /// <summary>Zero-padded so a store that sorts its keys lists them in sequence order.</summary>
    private static string SegmentKey(long seq) => SegmentPrefix + seq.ToString("D12");

    /// <summary>
    /// Oldest unacknowledged envelopes, in order, without removing them.
    ///
    /// Returned as COPIES, deliberately. Peeked envelopes are handed to a transport, and a
    /// transport — or anything between it and the controller — that mutates what it was given
    /// must corrupt its own copy, not the device's durable record. The queue's contents change
    /// through exactly two doors, <see cref="Enqueue"/> and <see cref="AcknowledgeThrough"/>,
    /// and a reference leak would quietly add a third.
    /// </summary>
    public IReadOnlyList<Envelope> Peek(int max) =>
        _pending.Take(Math.Max(0, max)).Select(Copy).ToList();

    private static Envelope Copy(Envelope envelope) => new()
    {
        Version = envelope.Version,
        Id = envelope.Id,
        MoundId = envelope.MoundId,
        Seq = envelope.Seq,
        SentAt = envelope.SentAt,
        Kind = envelope.Kind,
        Body = envelope.Body.Clone(),
        PrevDigest = envelope.PrevDigest,
        Signature = envelope.Signature
    };

    public void AcknowledgeThrough(long seq)
    {
        if (seq <= _ackedThrough) return;   // a stale or duplicate ack moves nothing backwards

        _ackedThrough = seq;
        foreach (var envelope in _pending.Where(e => e.Seq <= seq).ToList())
        {
            _pending.Remove(envelope);
            DropSegment(envelope.Seq);
        }
        PersistHead();
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>The head only: watermarks and counters, a few hundred bytes whatever the depth.</summary>
    private void PersistHead()
    {
        _store?.Put(StoreKey, JsonSerializer.Serialize(new QueueSnapshot
        {
            NextSeq = _nextSeq,
            LastDigest = _lastDigest,
            AckedThrough = _ackedThrough,
            Spilled = _spilled,
            Segmented = true
        }, ProtocolJson.Options));
    }

    private void RestoreFrom(string saved)
    {
        QueueSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<QueueSnapshot>(saved, ProtocolJson.Options);
        }
        catch (JsonException)
        {
            // A corrupt snapshot restores to an empty queue rather than to an exception at boot.
            // The records it held are lost and the controller will see the gap — which is the
            // honest outcome, because the chain makes loss detectable rather than deniable.
            return;
        }

        if (snapshot is null) return;

        _nextSeq = snapshot.NextSeq;
        _lastDigest = snapshot.LastDigest;
        _ackedThrough = snapshot.AckedThrough;
        _spilled = snapshot.Spilled;
        _pending.Clear();
        _segmentBytes.Clear();
        _pendingBytes = 0;

        if (!snapshot.Segmented)
        {
            // A queue written before `v0.9.34`, when the whole thing lived in this one document.
            // Migrated in place rather than dropped: those are signed records a controller has not
            // seen, and "we changed our storage format" is not a reason to put a gap in a chain.
            foreach (var envelope in snapshot.Pending.OrderBy(e => e.Seq))
            {
                var segment = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
                _pending.Add(envelope);
                _segmentBytes[envelope.Seq] = segment.Length;
                _pendingBytes += segment.Length;
                _store?.Put(SegmentKey(envelope.Seq), segment);
            }

            Trim();
            PersistHead();   // rewrites the head WITHOUT the inline pending array, once
            return;
        }

        // Segments, walked over the range the head describes. A missing one is a gap a spill left
        // behind (or a store that lost a file) — skipped, never treated as an error, because the
        // chain already carries the fact and refusing to boot over it would help nobody.
        for (var seq = _ackedThrough + 1; seq < _nextSeq; seq++)
        {
            if (_store is null || !_store.TryGet(SegmentKey(seq), out var raw)) continue;

            Envelope? envelope;
            try { envelope = JsonSerializer.Deserialize<Envelope>(raw, ProtocolJson.Options); }
            catch (JsonException) { continue; }
            if (envelope is null) continue;

            _pending.Add(envelope);
            _segmentBytes[seq] = raw.Length;
            _pendingBytes += raw.Length;
        }
    }

    private sealed class QueueSnapshot
    {
        [JsonPropertyName("next_seq")] public long NextSeq { get; set; }
        [JsonPropertyName("last_digest")] public string LastDigest { get; set; } = "";
        [JsonPropertyName("acked_through")] public long AckedThrough { get; set; } = -1;
        /// <summary>Unacknowledged envelopes dropped under pressure, awaiting report on the next beat.</summary>
        [JsonPropertyName("spilled")] public int Spilled { get; set; }
        /// <summary>False for a pre-`v0.9.34` document, whose envelopes are inline below and get migrated.</summary>
        [JsonPropertyName("segmented")] public bool Segmented { get; set; }
        /// <summary>Only populated by the old format. Retained so an upgrade migrates rather than loses.</summary>
        [JsonPropertyName("pending")] public List<Envelope> Pending { get; set; } = [];
    }
}
