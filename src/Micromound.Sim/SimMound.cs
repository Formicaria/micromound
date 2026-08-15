using System.Text.Json;
using Micromound.Protocol;

namespace Micromound.Sim;

/// <summary>
/// An in-memory Micromound: holds (at most) one active charter, runs a fake sensor, produces
/// hash-chained envelopes, and enforces the authority rules from MICROMOUND.md. Network-free —
/// the harness moves envelopes by method call, mirroring the homelab mock-provider approach.
/// </summary>
public sealed class SimMound(string moundId, string tier = SimMound.TierEdgeQueen)
{
    public const string TierEdgeQueen = "edge_queen";
    public const string TierController = "deterministic_controller";

    private readonly List<Envelope> _uplink = [];
    private long _seq;
    private string _lastDigest = "";
    private Charter? _charter;
    private DateTimeOffset _leaseExpiresAt = DateTimeOffset.MinValue;
    private bool _stopped;

    public string MoundId { get; } = moundId;
    public string Tier { get; } = tier;
    public bool IsReducedProfile => Tier == TierController;

    /// <summary>Fixed hardware truth for the simulated device.</summary>
    public IReadOnlySet<string> DeviceCapabilities { get; init; } =
        new HashSet<string> { "sense.temp", "act.relay_1" };

    public string State =>
        _stopped ? "stopped"
        : _charter is null ? "observe_only"
        : "chartered";

    public bool LeaseAlive(DateTimeOffset now) => _charter is not null && now < _leaseExpiresAt && !_stopped;

    /// <summary>Downlink: colony offers a charter. Invalid charters are refused, state untouched.</summary>
    public ValidationResult OfferCharter(Charter charter, DateTimeOffset now)
    {
        var result = CharterValidator.Validate(charter, MoundId, now, DeviceCapabilities);
        if (!result.IsValid) return result;
        _charter = charter; // complete replacement, never a diff
        _leaseExpiresAt = now.AddSeconds(charter.LeaseTtlSeconds);
        return result;
    }

    /// <summary>Sync beat succeeded: lease renews. Nothing else about authority changes.</summary>
    public void RenewLease(DateTimeOffset now)
    {
        if (_charter is not null && !_stopped)
            _leaseExpiresAt = now.AddSeconds(_charter.LeaseTtlSeconds);
    }

    /// <summary>
    /// Lease expiry check — call on the device's own clock tick. Expired ⇒ safe state; the
    /// charter is retained for reporting but authorizes nothing further.
    /// </summary>
    public bool QuiesceIfExpired(DateTimeOffset now)
    {
        if (_charter is null || now < _leaseExpiresAt) return false;
        _charter = null;
        return true;
    }

    /// <summary>Stop wins over everything and needs no charter.</summary>
    public void Stop() { _stopped = true; _charter = null; }

    /// <summary>
    /// Attempt an actuation. Refusals produce a refused ActionRecord (loud, never silent).
    /// </summary>
    public ActionRecord Actuate(string capability, DateTimeOffset now)
    {
        var record = new ActionRecord
        {
            ActionId = Guid.NewGuid().ToString(),
            CharterId = _charter?.CharterId ?? "",
            Capability = capability,
            StartedAt = now.ToString("O"),
            EndedAt = now.ToString("O")
        };

        if (_stopped) { record.Outcome = "stopped"; return record; }
        if (_charter is null || !LeaseAlive(now)) { record.Outcome = "refused"; return record; }
        if (!_charter.Capabilities.Contains(capability)) { record.Outcome = "refused"; return record; }
        if (!ActionClasses.TryParse(_charter.ActionCeiling, out var ceiling) || ceiling < ActionClass.Benign)
        { record.Outcome = "refused"; return record; }

        // Simulated success WITH evidence: a fake sensor delta proving the relay state changed.
        var evidence = new EvidenceItem
        {
            EvidenceId = Guid.NewGuid().ToString(),
            Type = "sensor_window",
            CapturedAt = now.ToString("O"),
            Source = "sim." + capability,
            PayloadJson = """{"before":0,"after":1}"""
        };
        EnqueueUplink(EnvelopeKinds.EvidenceBundle,
            new EvidenceBundle { BundleId = Guid.NewGuid().ToString(), Items = [evidence] }, now);

        record.Outcome = "succeeded";
        record.EvidenceRefs.Add(evidence.EvidenceId);
        EnqueueUplink(EnvelopeKinds.ActionRecord, record, now);
        return record;
    }

    /// <summary>Queue an uplink envelope, maintaining seq and the hash chain (works offline).</summary>
    public Envelope EnqueueUplink<T>(string kind, T body, DateTimeOffset now)
    {
        var envelope = new Envelope
        {
            MoundId = MoundId,
            Seq = _seq++,
            SentAt = now.ToString("O"),
            Kind = kind,
            Body = JsonSerializer.SerializeToElement(body, ProtocolJson.Options),
            PrevDigest = _lastDigest,
            Signature = "ed25519:sim" // real signing lands with the Edge Queen runtime (M2)
        };
        _lastDigest = envelope.Digest();
        _uplink.Add(envelope);
        return envelope;
    }

    /// <summary>Drain queued uplink (reconnect): oldest first, chain intact.</summary>
    public IReadOnlyList<Envelope> DrainUplink()
    {
        var drained = _uplink.ToList();
        _uplink.Clear();
        return drained;
    }
}
