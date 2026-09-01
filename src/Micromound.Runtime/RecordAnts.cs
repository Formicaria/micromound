using System.Text.Json;
using System.Text.Json.Serialization;
using Micromound.Capabilities;
using Micromound.Evidence;
using Micromound.Protocol;
using Micromound.Sync;

namespace Micromound.Runtime;

// The two ants that act on the RECORD rather than on the mission — ANTS.md, and the second half
// of the M2 seam. Scout, Forager and Guard sit on the path between a mission step and the
// hardware; Witness judges what the step may claim. These two decide what happens to the claim
// afterwards: the Cache Ant makes it survive a restart, and the Runner Ant makes it leave the
// mound. Neither one holds an executor, a driver, or any way to reach hardware.

/// <summary>
/// Short-term operational persistence — ANTS.md. An operational state store, not a knowledge
/// system: what it holds is exactly what a mound needs to come back safely after a power cut,
/// and nothing a mound could rebuild from its charter and its hardware.
///
/// Values are serialized with the protocol's own JSON options, for the same reason everything
/// else is: one encoding in the whole system, so a value written by one build reads in the next.
/// A value that fails to deserialize is treated as absent rather than as an error, because every
/// restore path in this repository answers a missing key the same way — start from observe-only,
/// which is never wrong, merely conservative.
/// </summary>
public sealed class CacheAnt(IStateStore store, WorkerDescriptor? descriptor = null) : ICacheAnt
{
    private const string Prefix = "cache:";
    private const string AuthorityKey = "authority";

    public WorkerDescriptor Descriptor { get; } = descriptor ?? new WorkerDescriptor
    {
        Name = DefaultAnts.Cache,
        Purpose = "operational persistence and restart recovery",
        RuntimeType = RuntimeTypes.Deterministic,
        Ceiling = ActionClass.Observe
    };

    public WorkerState State => WorkerState.Idle;

    public void Save<T>(string key, T value) =>
        store.Put(Prefix + key, JsonSerializer.Serialize(value, ProtocolJson.Options));

    public bool TryLoad<T>(string key, out T value)
    {
        value = default!;
        if (!store.TryGet(Prefix + key, out var saved)) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<T>(saved, ProtocolJson.Options);
            if (parsed is null) return false;
            value = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;   // corrupt is absent: the caller starts safe either way
        }
    }

    public void Delete(string key) => store.Delete(Prefix + key);

    // ---------------------------------------------------------------------------------------
    // Authority snapshots — the restart path
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Persist the mound's authority as it stands. Called after anything that changes it — a
    /// charter accepted, a lease renewed, a stop, a quiesce — so the snapshot on disk is never
    /// more permissive than the state in memory was.
    /// </summary>
    public void SaveAuthority(KernelAuthority authority) =>
        Save(AuthorityKey, new AuthoritySnapshot
        {
            Charter = authority.ActiveCharter,
            LeaseExpiresAt = authority.LeaseExpiresAt.ToWire(),
            Stopped = authority.IsStopped,
            Quiesced = authority.IsQuiesced,
            // The manifest tier rides in the snapshot too: an operator's configured narrowing
            // that evaporated on reboot would widen the effective bound to hardware ∩ charter,
            // and "never more permissive than the state in memory was" is the whole contract.
            DeviceLimits = new Dictionary<string, CapabilityLimits>(authority.DeviceLimits,
                StringComparer.Ordinal),
            SafeState = authority.SafeState
        });

    /// <summary>
    /// Rehydrate authority at process start. All the downward-resolving rules live in
    /// <see cref="KernelAuthority.Restore"/>; this is the plumbing that feeds it. False when no
    /// snapshot exists — a fresh mound, which starts observe-only like every mound.
    /// </summary>
    public bool TryRestoreAuthority(KernelAuthority authority, DateTimeOffset now,
        out ValidationResult result,
        IReadOnlySet<string>? deviceCapabilities = null, IReadOnlySet<string>? deviceRoutines = null)
    {
        result = new ValidationResult([]);
        if (!TryLoad<AuthoritySnapshot>(AuthorityKey, out var snapshot)) return false;

        ProtocolTime.TryParse(snapshot.LeaseExpiresAt, out var leaseExpiresAt);
        result = authority.Restore(snapshot.Charter, leaseExpiresAt, snapshot.Stopped,
            snapshot.Quiesced, now, deviceCapabilities, deviceRoutines,
            snapshot.DeviceLimits, snapshot.SafeState);
        return true;
    }

    /// <summary>What a restart needs to know about authority, and nothing else.</summary>
    public sealed class AuthoritySnapshot
    {
        [JsonPropertyName("charter")] public Charter? Charter { get; set; }
        [JsonPropertyName("lease_expires_at")] public string LeaseExpiresAt { get; set; } = "";
        [JsonPropertyName("stopped")] public bool Stopped { get; set; }
        [JsonPropertyName("quiesced")] public bool Quiesced { get; set; }
        [JsonPropertyName("device_limits")] public Dictionary<string, CapabilityLimits> DeviceLimits { get; set; } = [];
        [JsonPropertyName("safe_state")] public string SafeState { get; set; } = "";
    }
}

/// <summary>
/// A mission in flight — the operational state a restart needs to answer one question deterministically:
/// what happened to the mission the process was running when it died?
///
/// It is written before a mission runs and cleared the moment it finishes (through the coordinator's
/// single <c>Finish</c> funnel). The one path that does NOT reach <c>Finish</c> is a process death
/// mid-mission, and that is exactly the path that leaves this behind. It is not a resume point: the
/// synchronous runtime does not replay half-run physical steps. It exists so the restart reports the
/// interruption instead of losing the mission, and — via <see cref="ActuationInFlight"/> — so the
/// one genuinely dangerous window is reported as such rather than papered over.
///
/// <see cref="ActuationInFlight"/> carries the id of a step whose actuation was dispatched to
/// hardware but whose result had not yet been recorded when this was last written. A death in that
/// gap is inherently ambiguous — the actuation may or may not have physically happened, and there is
/// no evidence either way — so the recovery path fails closed: it neither replays the actuation nor
/// assumes it succeeded. Empty means no actuation is in that window.
/// </summary>
public sealed class MissionCheckpoint
{
    /// <summary>Cache key (the Cache Ant prefixes it). One mission runs at a time, so one slot.</summary>
    public const string Key = "mission";

    [JsonPropertyName("mission_id")] public string MissionId { get; set; } = "";
    [JsonPropertyName("charter_id")] public string CharterId { get; set; } = "";
    [JsonPropertyName("started_at")] public string StartedAt { get; set; } = "";
    /// <summary>
    /// The interrupted mission's declared safe state. The M4 host reads this to bring physical
    /// outputs a half-run mission may have left energized into the mission's own safe state on a
    /// cold start; persisted now so the recovery need is captured where the interruption is.
    /// </summary>
    [JsonPropertyName("safe_state")] public string SafeState { get; set; } = "";
    /// <summary>Step id whose actuation was dispatched but not yet settled; empty when none is in flight.</summary>
    [JsonPropertyName("actuation_in_flight")] public string ActuationInFlight { get; set; } = "";

    public static MissionCheckpoint Of(Mission mission, DateTimeOffset now) => new()
    {
        MissionId = mission.MissionId,
        CharterId = mission.CharterId,
        StartedAt = now.ToWire(),
        SafeState = mission.SafeState
    };
}

/// <summary>What one sync attempt did — for the caller's log and the colony view, not for control flow.</summary>
public sealed class SyncOutcome
{
    public required bool Delivered { get; init; }
    public int EnvelopesSent { get; init; }
    public int DownlinkHandled { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>
/// Secure communication with the upstream controller — ANTS.md, and the only outward-facing
/// worker. Everything else in the mound is local by construction.
///
/// The Runner owns none of the policy it carries. It signs what the queue chained, verifies what
/// the controller signed, and hands each verified envelope to the component whose decision it is:
/// stops and charters to the coordinator, acks to the queue and the evidence store, missions to
/// the coordinator's executor. A Runner that interpreted anything would be a second authority
/// path, and there is exactly one thing in this system that says yes.
///
/// Downlink ordering is PROTOCOL.md §7's: stops are processed ahead of ALL other downlink in the
/// same exchange, before any charter is accepted and before any mission runs — a batch that
/// carries both a mission and a stop executes the stop and refuses the mission, in that order,
/// regardless of the order they arrived in.
/// </summary>
public sealed class RunnerAnt : IRunnerAnt
{
    private readonly IMoundMajor _mound;
    private readonly IUplinkQueue _queue;
    private readonly ISyncTransport _transport;
    private readonly IEnvelopeSigner _signer;
    private readonly IEnvelopeVerifier _verifier;
    private readonly IEvidenceStore? _evidence;
    private readonly HashSet<string> _handledDownlink = new(StringComparer.Ordinal);
    private readonly List<string> _audit = [];

    public RunnerAnt(IMoundMajor mound, IUplinkQueue queue, ISyncTransport transport,
        IEnvelopeSigner signer, IEnvelopeVerifier verifier, IEvidenceStore? evidence = null,
        WorkerDescriptor? descriptor = null)
    {
        _mound = mound;
        _queue = queue;
        _transport = transport;
        _signer = signer;
        _verifier = verifier;
        _evidence = evidence;

        Descriptor = descriptor ?? new WorkerDescriptor
        {
            Name = DefaultAnts.Runner,
            Purpose = "enrollment, signed sync, durable retry, backlog drain",
            RuntimeType = RuntimeTypes.Deterministic,
            Ceiling = ActionClass.Observe
        };
    }

    public WorkerDescriptor Descriptor { get; }

    public WorkerState State => IsConnected ? WorkerState.Idle : WorkerState.Degraded;

    public bool IsConnected { get; private set; }

    public DateTimeOffset? LastSyncAt { get; private set; }

    /// <summary>Dropped downlink, with reasons. "Dropped and audited" is only auditable if this survives.</summary>
    public IReadOnlyList<string> Audit => _audit;

    /// <summary>Enqueue a pre-built, pre-signed envelope. The queue enforces the chain.</summary>
    public void Enqueue(Envelope envelope) => _queue.Enqueue(envelope);

    /// <summary>
    /// Build, sign, chain, and queue one uplink envelope. This is the only envelope factory on
    /// the mound: sequence and anchor come from the queue, the signature from the device key, and
    /// there is no path that produces an envelope outside the chain.
    /// </summary>
    public Envelope Publish<T>(string kind, T body, DateTimeOffset now)
    {
        var envelope = new Envelope
        {
            MoundId = _mound.MoundId,
            Seq = _queue.NextSeq,
            SentAt = now.ToWire(),
            Kind = kind,
            Body = JsonSerializer.SerializeToElement(body, ProtocolJson.Options),
            PrevDigest = _queue.LastDigest
        };

        EnvelopeSigning.Sign(envelope, _signer);
        _queue.Enqueue(envelope);
        return envelope;
    }

    /// <summary>
    /// One sync beat — PROTOCOL.md §1 and §5. Queue the beat, drain the backlog oldest-first,
    /// then handle what came down. Offline is a normal state: a failed exchange leaves everything
    /// queued and returns, and the next beat tries again from exactly where this one stopped.
    /// </summary>
    public SyncOutcome Sync(DateTimeOffset now, int batchSize = 64)
    {
        var beat = Publish(EnvelopeKinds.MoundSync, new
        {
            state = _mound.State,
            queue_depth = _queue.Depth
        }, now);

        var deferred = new List<Envelope>();
        var sent = 0;
        var beatAcknowledged = false;

        // Drain oldest-first. Acks are handled inline because the drain's own progress depends on
        // them; everything else waits until the drain settles, so that a stop in the batch is
        // processed before any mission in the same batch, wherever each arrived.
        while (true)
        {
            var batch = _queue.Peek(batchSize);
            if (batch.Count == 0) break;

            var depthBefore = _queue.Depth;

            foreach (var envelope in batch)
            {
                if (!_transport.TryExchange(envelope, out var downlink, out var detail))
                {
                    // What already came down still gets handled — a stop received a moment before
                    // the link died must not wait for the link to come back.
                    IsConnected = false;
                    var handledOffline = HandleDeferred(SortForHandling(deferred), now);
                    return new SyncOutcome
                    {
                        Delivered = false, EnvelopesSent = sent, DownlinkHandled = handledOffline,
                        Detail = string.IsNullOrEmpty(detail) ? "transport unavailable" : detail
                    };
                }

                sent++;

                foreach (var received in downlink)
                {
                    if (!Verify(received)) continue;

                    if (received.Kind == EnvelopeKinds.Ack)
                        beatAcknowledged |= HandleAck(received, beat.Seq);
                    else
                        deferred.Add(received);
                }
            }

            // No ack removed anything: everything has been offered once, and re-sending inside
            // the same beat would be a hot loop, not persistence. The records stay queued.
            if (_queue.Depth >= depthBefore) break;
        }

        IsConnected = true;
        LastSyncAt = now;

        // PROTOCOL.md §5: it is the ACCEPTED beat that renews the lease — the controller's
        // acknowledgement covering the beat's sequence number, not the transport returning.
        if (beatAcknowledged) _mound.RenewLease(now);

        var handled = HandleDeferred(SortForHandling(deferred), now);

        return new SyncOutcome
        {
            Delivered = true, EnvelopesSent = sent, DownlinkHandled = handled,
            Detail = beatAcknowledged ? "" : "delivered, but the controller did not acknowledge the beat"
        };
    }

    // ---------------------------------------------------------------------------------------

    private bool Verify(Envelope envelope)
    {
        // Idempotent across re-delivery: a controller that resends downlink because its own ack
        // was lost must not make the mound accept the same charter twice or run a mission again.
        if (_handledDownlink.Contains(envelope.Id)) return false;

        // Addressed to THIS mound. A stop body carries no mound id of its own, so a misrouted
        // stop would otherwise stop whichever mound it happened to reach — and a controller bug
        // that crosses two downlink streams should surface as an audit line, not as obedience.
        if (!string.Equals(envelope.MoundId, _mound.MoundId, StringComparison.Ordinal))
        {
            _audit.Add($"downlink {envelope.Id} ({envelope.Kind}) dropped: addressed to " +
                       $"'{envelope.MoundId}', this mound is '{_mound.MoundId}'");
            return false;
        }

        var check = EnvelopeValidator.Validate(envelope, _verifier, KeyIds.Controller);
        if (check.IsValid) return true;

        // Dropped and audited, never processed — PROTOCOL.md §2. Not acknowledged either: an ack
        // for an envelope nobody processed would tell the controller it was.
        _audit.Add($"downlink {envelope.Id} ({envelope.Kind}) dropped: {string.Join("; ", check.Errors)}");
        return false;
    }

    /// <summary>Stops first, then authority, then configuration, then work — PROTOCOL.md §7.</summary>
    private static List<Envelope> SortForHandling(List<Envelope> deferred) =>
        deferred.OrderBy(e => e.Kind switch
        {
            EnvelopeKinds.Stop => 0,
            EnvelopeKinds.Charter => 1,
            EnvelopeKinds.Config => 2,
            EnvelopeKinds.Mission => 3,
            _ => 4
        }).ToList();

    private bool HandleAck(Envelope envelope, long beatSeq)
    {
        _handledDownlink.Add(envelope.Id);

        var ack = Deserialize<AckBody>(envelope);
        if (ack is null || ack.ThroughSeq < 0) return false;

        _queue.AcknowledgeThrough(ack.ThroughSeq);
        if (ack.EvidenceIds.Count > 0) _evidence?.Acknowledge(ack.EvidenceIds);

        return ack.ThroughSeq >= beatSeq;
    }

    private int HandleDeferred(List<Envelope> deferred, DateTimeOffset now)
    {
        var handled = 0;

        foreach (var envelope in deferred)
        {
            // The receive-time check catches re-delivery across syncs; this one catches two
            // copies inside the SAME batch — Add returning false is the second copy. Without it,
            // a controller whose ack was lost mid-exchange could make one mission run twice.
            if (!_handledDownlink.Add(envelope.Id)) continue;
            handled++;

            switch (envelope.Kind)
            {
                case EnvelopeKinds.Stop:
                    // Needs no valid charter and precedes everything — which SortForHandling has
                    // already guaranteed by the time this line runs.
                    _mound.Stop();
                    Publish(EnvelopeKinds.Ack, new AckBody
                    {
                        RefersTo = envelope.Id,
                        Detail = "stopped; actuation ceased, sensing and syncing continue"
                    }, now);
                    break;

                case EnvelopeKinds.Charter:
                {
                    var charter = Deserialize<Charter>(envelope);
                    var result = charter is null
                        ? new ValidationResult(["charter body unreadable"])
                        : _mound.AcceptCharter(charter, now);

                    if (!result.IsValid)
                        Publish(EnvelopeKinds.Ack, new AckBody
                        {
                            Status = AckStatuses.Refused,
                            RefersTo = envelope.Id,
                            Detail = "charter refused: " + string.Join("; ", result.Errors)
                        }, now);
                    break;
                }

                case EnvelopeKinds.Config:
                {
                    var manifest = Deserialize<MoundManifest>(envelope);
                    var result = manifest is null
                        ? new ValidationResult(["config body unreadable"])
                        : _mound.ApplyManifest(manifest, now);

                    if (!result.IsValid)
                        Publish(EnvelopeKinds.Ack, new AckBody
                        {
                            Status = AckStatuses.Refused,
                            RefersTo = envelope.Id,
                            Detail = "config refused, previous manifest stays in force: " +
                                     string.Join("; ", result.Errors)
                        }, now);
                    break;
                }

                case EnvelopeKinds.Mission:
                {
                    var mission = Deserialize<Mission>(envelope);
                    if (mission is null)
                    {
                        _audit.Add($"mission {envelope.Id} dropped: body unreadable");
                        break;
                    }

                    // The coordinator executes; the Runner only reports what it said. The report
                    // goes up whatever the verdict was — a refused mission is reported exactly
                    // like a completed one. The durable in-flight checkpoint is cleared only AFTER
                    // the report is queued (Publish enqueues to the durable uplink), so a crash
                    // between the two re-reports the mission rather than losing it.
                    var report = _mound.Execute(mission, now);
                    Publish(EnvelopeKinds.MissionReport, report, now);
                    _mound.ClearMissionCheckpoint();
                    break;
                }

                default:
                    // Refusal is loud, never silent — PROTOCOL.md §2. This covers kinds the
                    // protocol knows but a mound never accepts as downlink, too: an
                    // `action_record` arriving downhill is exactly as unprocessable as a kind
                    // from the future.
                    Publish(EnvelopeKinds.Ack, new AckBody
                    {
                        Status = AckStatuses.RefusedUnknownKind,
                        RefersTo = envelope.Id,
                        Detail = $"'{envelope.Kind}' is not a downlink kind this mound processes"
                    }, now);
                    break;
            }
        }

        return handled;
    }

    private static T? Deserialize<T>(Envelope envelope) where T : class
    {
        try
        {
            return envelope.Body.Deserialize<T>(ProtocolJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
