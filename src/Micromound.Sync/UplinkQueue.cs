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
/// Every mutation persists the whole queue through the <see cref="IStateStore"/>, so a power cut
/// between enqueue and drain loses nothing. With no store supplied the queue is memory-only,
/// which is the simulator's mode and an explicit choice, never a fallback.
/// </summary>
public sealed class DurableUplinkQueue : IUplinkQueue
{
    private const string StoreKey = "sync:uplink-queue";

    private readonly IStateStore? _store;
    private readonly List<Envelope> _pending = [];
    private long _nextSeq;
    private string _lastDigest = "";
    private long _ackedThrough = -1;

    public DurableUplinkQueue(IStateStore? store = null)
    {
        _store = store;

        if (_store is not null && _store.TryGet(StoreKey, out var saved))
            RestoreFrom(saved);
    }

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
        Persist();
    }

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
        _pending.RemoveAll(e => e.Seq <= seq);
        Persist();
    }

    // ---------------------------------------------------------------------------------------

    private void Persist()
    {
        _store?.Put(StoreKey, JsonSerializer.Serialize(new QueueSnapshot
        {
            NextSeq = _nextSeq,
            LastDigest = _lastDigest,
            AckedThrough = _ackedThrough,
            Pending = _pending
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
        _pending.Clear();
        _pending.AddRange(snapshot.Pending.OrderBy(e => e.Seq));
    }

    private sealed class QueueSnapshot
    {
        [JsonPropertyName("next_seq")] public long NextSeq { get; set; }
        [JsonPropertyName("last_digest")] public string LastDigest { get; set; } = "";
        [JsonPropertyName("acked_through")] public long AckedThrough { get; set; } = -1;
        [JsonPropertyName("pending")] public List<Envelope> Pending { get; set; } = [];
    }
}
