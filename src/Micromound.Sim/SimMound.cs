using System.Globalization;
using System.Text.Json;
using Micromound.Crypto;
using Micromound.Protocol;

namespace Micromound.Sim;

/// <summary>
/// An in-memory Micromound: holds (at most) one active charter, runs a fake sensor, produces
/// signed hash-chained envelopes, and enforces the authority rules from MICROMOUND.md plus the
/// Layer 1 clamping and evidence gating from SAFETY.md. Network-free — the harness moves
/// envelopes by method call, mirroring the homelab mock-provider approach.
/// </summary>
public sealed class SimMound(string moundId, string tier = SimMound.TierEdgeQueen)
{
    public const string TierEdgeQueen = "edge_queen";
    public const string TierController = "deterministic_controller";

    private readonly List<Envelope> _uplink = [];
    private readonly Dictionary<string, EvidenceItem> _evidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastActuationEnd = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _actuationHistory = new(StringComparer.Ordinal);

    private long _seq;
    private string _lastDigest = "";
    private Charter? _charter;
    private DateTimeOffset _leaseExpiresAt = DateTimeOffset.MinValue;
    private bool _quiesced;
    private bool _stopped;
    private Ed25519EnvelopeSigner? _signer;

    public string MoundId { get; } = moundId;
    public string Tier { get; } = tier;
    public bool IsReducedProfile => Tier == TierController;

    /// <summary>
    /// The device identity — generated on-device, private half never leaves (PROTOCOL.md §3).
    /// Settable at construction so tests can pin a deterministic key.
    /// </summary>
    public Ed25519KeyPair Keys { get; init; } = Ed25519KeyPair.Generate();

    public byte[] PublicKey => Keys.PublicKey;

    private Ed25519EnvelopeSigner Signer => _signer ??= new Ed25519EnvelopeSigner(MoundId, Keys);

    /// <summary>Fixed hardware truth for the simulated device.</summary>
    public IReadOnlySet<string> DeviceCapabilities { get; init; } =
        new HashSet<string> { "sense.temp", "act.relay_1" };

    /// <summary>
    /// Firmware-compiled limits — the outer bound a charter can only narrow (SAFETY.md Layer 1).
    /// These stand in for what an ESP32 build would enumerate at compile time.
    /// </summary>
    public Dictionary<string, CapabilityLimits> FirmwareLimits { get; init; } = new(StringComparer.Ordinal)
    {
        ["act.relay_1"] = new CapabilityLimits { MaxOnSeconds = 30, MinOffSeconds = 300, MaxRatePerHour = 6 }
    };

    /// <summary>
    /// Flip to false to simulate a dead sensor: the actuation still happens, but nothing observes
    /// it, so the record must come back `unverified`. Commands are not evidence.
    /// </summary>
    public bool SensorHealthy { get; set; } = true;

    public string State =>
        _stopped ? "stopped"
        : _charter is null ? "observe_only"
        : _quiesced ? "quiesced"
        : "chartered";

    public bool LeaseAlive(DateTimeOffset now) =>
        _charter is not null && !_quiesced && !_stopped && now < _leaseExpiresAt;

    /// <summary>Downlink: colony offers a charter. Invalid charters are refused, state untouched.</summary>
    public ValidationResult OfferCharter(Charter charter, DateTimeOffset now)
    {
        var result = CharterValidator.Validate(charter, MoundId, now, DeviceCapabilities);
        if (!result.IsValid) return result;

        _charter = charter; // complete replacement, never a diff
        _leaseExpiresAt = now.AddSeconds(charter.LeaseTtlSeconds);
        _quiesced = false;  // a fresh charter is the only way out of quiesce
        return result;
    }

    /// <summary>Sync beat succeeded: lease renews. Nothing else about authority changes.</summary>
    public void RenewLease(DateTimeOffset now)
    {
        if (_charter is not null && !_stopped && !_quiesced)
            _leaseExpiresAt = now.AddSeconds(_charter.LeaseTtlSeconds);
    }

    /// <summary>
    /// Lease expiry check — call on the device's own clock tick. Expired ⇒ safe state. The
    /// charter is retained for reporting but authorizes nothing further, and the mound queues a
    /// `quiesced` report for the colony to read on reconnect (PROTOCOL.md §5).
    /// </summary>
    public bool QuiesceIfExpired(DateTimeOffset now)
    {
        if (_charter is null || _quiesced || _stopped || now < _leaseExpiresAt) return false;

        _quiesced = true;
        EnqueueUplink(EnvelopeKinds.MoundSync, new
        {
            state = "quiesced",
            charter_id = _charter.CharterId,
            safe_state = _charter.SafeState,
            lease_expired_at = _leaseExpiresAt.ToWire()
        }, now);

        return true;
    }

    /// <summary>Stop wins over everything and needs no charter.</summary>
    public void Stop()
    {
        _stopped = true;
        _charter = null;
        _quiesced = false;
    }

    /// <summary>
    /// Attempt an actuation. Every path produces an ActionRecord carrying its reason — refusals
    /// and clamps are loud, never silent. <paramref name="requestedOnSeconds"/> is what the
    /// mission asked for; what actually runs is the intersection of firmware and charter limits.
    /// </summary>
    public ActionRecord Actuate(string capability, DateTimeOffset now, double? requestedOnSeconds = null)
    {
        var record = new ActionRecord
        {
            ActionId = Guid.NewGuid().ToString(),
            CharterId = _charter?.CharterId ?? "",
            Capability = capability,
            StartedAt = now.ToWire(),
            EndedAt = now.ToWire()
        };

        if (_stopped)
            return Refuse(record, ActionOutcomes.Stopped, "mound is stopped; stop precedes all work", now);
        if (_charter is null)
            return Refuse(record, ActionOutcomes.Refused, "no charter: observe only", now);
        if (_quiesced)
            return Refuse(record, ActionOutcomes.Refused, "lease expired; awaiting a fresh charter", now);
        if (!LeaseAlive(now))
            return Refuse(record, ActionOutcomes.Refused, "lease is not alive", now);

        if (!_charter.Capabilities.Contains(capability))
            return Refuse(record, ActionOutcomes.Refused,
                $"capability '{capability}' is not in the charter", now);

        if (!ActionClasses.TryParse(_charter.ActionCeiling, out var ceiling))
            return Refuse(record, ActionOutcomes.Refused,
                $"action_ceiling unknown: '{_charter.ActionCeiling}'", now);

        if (ceiling < ActionClass.Benign)
            return Refuse(record, ActionOutcomes.Refused,
                $"action_ceiling '{_charter.ActionCeiling}' does not admit actuation", now);

        // SAFETY.md Layer 1: the narrower of firmware and charter always wins.
        var effective = EffectiveLimits(capability);

        if (effective.MinOffSeconds is { } minOff &&
            _lastActuationEnd.TryGetValue(capability, out var lastEnd) &&
            now < lastEnd.AddSeconds(minOff))
        {
            return Refuse(record, ActionOutcomes.Refused,
                $"duty cycle: min_off_s {minOff} not elapsed since {lastEnd.ToWire()}", now);
        }

        if (effective.MaxRatePerHour is { } maxRate && RecentActuations(capability, now) >= maxRate)
        {
            return Refuse(record, ActionOutcomes.Refused,
                $"rate limit: max_rate_per_h {maxRate} reached", now);
        }

        var requested = requestedOnSeconds ?? effective.MaxOnSeconds ?? 1.0;
        var clamped = LimitClamp.ClampOnSeconds(requested, effective, out var allowed);

        record.Parameters["on_s"] = allowed;
        record.EndedAt = now.AddSeconds(allowed).ToWire();
        record.Outcome = clamped ? ActionOutcomes.Clamped : ActionOutcomes.Succeeded;
        if (clamped)
            record.Detail = $"on_s narrowed {requested} -> {allowed} by max_on_s {effective.MaxOnSeconds}";

        // Independent observation of the result — or the absence of one.
        if (SensorHealthy)
        {
            var evidence = new EvidenceItem
            {
                EvidenceId = Guid.NewGuid().ToString(),
                Type = "sensor_window",
                CapturedAt = now.ToWire(),
                Source = "sim." + capability,
                PayloadJson =
                    $$"""{"before":0,"after":1,"on_s":{{allowed.ToString(CultureInfo.InvariantCulture)}}}"""
            };

            _evidence[evidence.EvidenceId] = evidence;
            record.EvidenceRefs.Add(evidence.EvidenceId);

            EnqueueUplink(EnvelopeKinds.EvidenceBundle,
                new EvidenceBundle { BundleId = Guid.NewGuid().ToString(), Items = [evidence] }, now);
        }

        // "Commands are not evidence": the optimistic outcome only survives the gate if
        // something independent actually observed the work.
        var gated = EvidenceGate.Gate(record, _charter.Evidence, _evidence, now, out var reason);
        if (!string.Equals(gated, record.Outcome, StringComparison.Ordinal))
        {
            record.Outcome = gated;
            record.Detail = reason;
        }

        _lastActuationEnd[capability] = now.AddSeconds(allowed);
        History(capability).Add(now);

        EnqueueUplink(EnvelopeKinds.ActionRecord, record, now);
        return record;
    }

    /// <summary>The bound actually enforced for a capability: firmware ∩ charter.</summary>
    public CapabilityLimits EffectiveLimits(string capability)
    {
        var firmware = FirmwareLimits.TryGetValue(capability, out var f) ? f : new CapabilityLimits();
        var charter = _charter is not null && _charter.Limits.TryGetValue(capability, out var c)
            ? c
            : new CapabilityLimits();

        return LimitClamp.Intersect(firmware, charter);
    }

    /// <summary>Queue an uplink envelope, signed and chained (works offline).</summary>
    public Envelope EnqueueUplink<T>(string kind, T body, DateTimeOffset now)
    {
        var envelope = new Envelope
        {
            MoundId = MoundId,
            Seq = _seq++,
            SentAt = now.ToWire(),
            Kind = kind,
            Body = JsonSerializer.SerializeToElement(body, ProtocolJson.Options),
            PrevDigest = _lastDigest
        };

        EnvelopeSigning.Sign(envelope, Signer);

        // The digest covers everything except `sig`, so signing does not disturb the chain.
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

    /// <summary>Evidence the mound has produced, by id — what the colony resolves refs against.</summary>
    public IReadOnlyDictionary<string, EvidenceItem> Evidence => _evidence;

    /// <summary>
    /// SAFETY.md: "every refusal, clamp, trip, and validation failure is reported and audited" —
    /// so a refusal queues its record for the colony exactly like a success does.
    /// </summary>
    private ActionRecord Refuse(ActionRecord record, string outcome, string detail, DateTimeOffset now)
    {
        record.Outcome = outcome;
        record.Detail = detail;
        EnqueueUplink(EnvelopeKinds.ActionRecord, record, now);
        return record;
    }

    private List<DateTimeOffset> History(string capability)
    {
        if (!_actuationHistory.TryGetValue(capability, out var list))
        {
            list = [];
            _actuationHistory[capability] = list;
        }

        return list;
    }

    private int RecentActuations(string capability, DateTimeOffset now)
    {
        var window = now.AddHours(-1);
        return History(capability).Count(at => at > window);
    }
}
