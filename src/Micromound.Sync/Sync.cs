using Micromound.Protocol;

namespace Micromound.Sync;

/// <summary>
/// The device-initiated transport — PROTOCOL.md §1. Mounds dial the controller; the controller
/// never needs to reach into the device network, which is what lets a mound sit behind NAT on a
/// residential connection without any inbound path existing at all.
/// </summary>
public interface ISyncTransport
{
    /// <summary>
    /// POST one signed envelope and return whatever downlink came back. Offline is a normal
    /// state, not an error: an implementation that cannot reach the controller reports that and
    /// the queue drains later.
    /// </summary>
    bool TryExchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail);
}

/// <summary>
/// The durable uplink queue. Survives process restart and device power loss, drains oldest-first
/// on reconnect, and preserves the hash chain across the gap — a backlog that reordered itself
/// would be indistinguishable from one that had been tampered with.
/// </summary>
public interface IUplinkQueue
{
    void Enqueue(Envelope envelope);

    /// <summary>Peek the oldest unacknowledged envelopes without removing them.</summary>
    IReadOnlyList<Envelope> Peek(int max);

    /// <summary>Remove envelopes the controller acknowledged, by sequence number.</summary>
    void AcknowledgeThrough(long seq);

    long NextSeq { get; }

    /// <summary>Digest of the last enqueued envelope — the next one's <c>prev_digest</c>.</summary>
    string LastDigest { get; }

    int Depth { get; }
}

/// <summary>
/// Enrollment — PROTOCOL.md §3. The device generates its own keypair, the private half never
/// leaves, and the one-time token is burned on use. There is no self-service re-key: a reflash or
/// a rotation needs a fresh operator-minted token, because a device that can re-key itself is a
/// device whose compromise is permanent.
/// </summary>
public interface IEnrollmentClient
{
    /// <summary>
    /// Present the one-time token and this device's public key; receive the controller's public
    /// key in return. Returns false with a reason on refusal — a burned or unknown token is a
    /// definite answer, not a retry.
    /// </summary>
    bool TryEnroll(string token, byte[] devicePublicKey, out byte[] controllerPublicKey, out string detail);
}

/// <summary>
/// Sync beat scheduling — PROTOCOL.md §1. Interval comes from the charter (15 s for a Pi-class
/// mound, 60 s for a controller), with exponential backoff and jitter on failure so a controller
/// coming back up is not met by an entire fleet retrying in lockstep.
/// </summary>
public sealed class SyncSchedule(int baseIntervalSeconds, int maxIntervalSeconds = 900)
{
    private int _consecutiveFailures;

    public int BaseIntervalSeconds { get; } = Math.Max(1, baseIntervalSeconds);
    public int MaxIntervalSeconds { get; } = Math.Max(1, maxIntervalSeconds);

    public int ConsecutiveFailures => _consecutiveFailures;

    public void RecordSuccess() => _consecutiveFailures = 0;

    public void RecordFailure() => _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 16);

    /// <summary>
    /// Seconds until the next attempt. <paramref name="jitterFraction"/> is supplied by the
    /// caller rather than drawn here, so the schedule stays a pure function and its tests do not
    /// depend on a random source.
    /// </summary>
    public double NextDelaySeconds(double jitterFraction = 0)
    {
        var backoff = BaseIntervalSeconds * Math.Pow(2, _consecutiveFailures);
        var capped = Math.Min(backoff, MaxIntervalSeconds);
        var jitter = capped * Math.Clamp(jitterFraction, -0.5, 0.5);
        return Math.Max(1, capped + jitter);
    }
}
