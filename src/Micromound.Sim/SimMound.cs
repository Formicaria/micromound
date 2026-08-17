using System.Globalization;
using System.Text.Json;
using Micromound.Capabilities;
using Micromound.Crypto;
using Micromound.Protocol;

namespace Micromound.Sim;

/// <summary>
/// An in-memory Micromound: a real <see cref="CapabilityKernel"/> over fake hardware.
///
/// The point of the simulator is that it does NOT reimplement the authority rules. It builds a
/// capability registry from a fake hardware profile, hands it to the same kernel a Pi-class mound
/// runs, and lets that kernel answer every question about charters, leases, limits, duty cycles,
/// and evidence. If the simulator and the runtime could disagree, the simulator would be proving
/// nothing.
///
/// Network-free: the harness moves envelopes by method call.
/// </summary>
public sealed class SimMound(string moundId, string tier = SimMound.TierMoundMajor)
{
    /// <summary>Pi-class: full runtime, full envelope set, a Mound Major coordinating ants.</summary>
    public const string TierMoundMajor = "mound_major";

    /// <summary>ESP32-class: compiled routines, reduced envelope set, no open-ended planning.</summary>
    public const string TierController = "deterministic_controller";

    private readonly List<Envelope> _uplink = [];
    private readonly Dictionary<string, EvidenceItem> _evidence = new(StringComparer.Ordinal);

    private long _seq;
    private string _lastDigest = "";
    private Ed25519EnvelopeSigner? _signer;

    // Built lazily so that every `init` property below is already set. A constructor-built kernel
    // would capture the default hardware profile and silently ignore a caller's own.
    private CapabilityKernel? _kernel;

    public string MoundId { get; } = moundId;

    public string Tier { get; } = tier;

    public bool IsReducedProfile => Tier == TierController;

    /// <summary>
    /// The device identity — generated on-device, private half never leaves (PROTOCOL.md §3).
    /// Settable at construction so tests can pin a deterministic key.
    /// </summary>
    public Ed25519KeyPair Keys { get; init; } = Ed25519KeyPair.Generate();

    public byte[] PublicKey => Keys.PublicKey;

    /// <summary>Fixed hardware truth for the simulated device.</summary>
    public IReadOnlySet<string> DeviceCapabilities { get; init; } =
        new HashSet<string>(StringComparer.Ordinal) { "sense.temp", "act.relay_1" };

    /// <summary>
    /// Firmware-compiled limits — the innermost tier a charter can only narrow (SAFETY.md Layer 1).
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

    private Ed25519EnvelopeSigner Signer => _signer ??= new Ed25519EnvelopeSigner(MoundId, Keys);

    /// <summary>The authority state the kernel consults — charter, lease, stop, device limits.</summary>
    public KernelAuthority Authority => Kernel.Authority;

    /// <summary>The one physical authority boundary. The simulator holds no other path to hardware.</summary>
    public CapabilityKernel Kernel
    {
        get
        {
            if (_kernel is not null) return _kernel;

            var capabilities = new CapabilityRegistry();

            foreach (var id in DeviceCapabilities)
            {
                var isSensor = CapabilityId.IsSense(id);

                capabilities.Register(new CapabilityDescriptor
                {
                    Id = id,
                    Class = isSensor ? ActionClass.Observe : ActionClass.Benign,
                    Description = "simulated " + id,
                    HardwareLimits = FirmwareLimits.TryGetValue(id, out var limits) ? limits : new CapabilityLimits(),
                    Parameters = isSensor
                        ? new HashSet<string>(StringComparer.Ordinal)
                        : new HashSet<string>(StringComparer.Ordinal) { "on_s" },
                    DurationParameter = isSensor ? null : "on_s"
                });
            }

            var routines = new RoutineRegistry(capabilities);
            var kernel = new CapabilityKernel(capabilities, routines, new KernelAuthority(MoundId));

            foreach (var id in capabilities.Ids)
                kernel.RegisterExecutor(new SimExecutor(id, this));

            _kernel = kernel;
            return kernel;
        }
    }

    public string State => Authority.State;

    public bool LeaseAlive(DateTimeOffset now) => Authority.LeaseAlive(now);

    /// <summary>Downlink: the controller offers a charter. Invalid charters are refused, state untouched.</summary>
    public ValidationResult OfferCharter(Charter charter, DateTimeOffset now) =>
        Authority.AcceptCharter(charter, now, Kernel.Capabilities.DeclaredCapabilities(),
            Kernel.Routines.DeclaredRoutines());

    /// <summary>Apply a declarative configuration manifest — the middle limit tier and the safe state.</summary>
    public ValidationResult ApplyManifest(MoundManifest manifest)
    {
        var result = ManifestValidator.Validate(manifest, MoundId);
        if (result.IsValid) Authority.ApplyManifest(manifest);
        return result;
    }

    /// <summary>Sync beat acknowledged: the lease renews. Nothing else about authority changes.</summary>
    public void RenewLease(DateTimeOffset now) => Authority.RenewLease(now);

    /// <summary>
    /// Lease expiry check — call on the device's own clock tick. Expired ⇒ safe state. The
    /// charter is retained for reporting but authorizes nothing further, and the mound queues a
    /// `quiesced` report for the controller to read on reconnect (PROTOCOL.md §5).
    /// </summary>
    public bool QuiesceIfExpired(DateTimeOffset now)
    {
        var charterId = Authority.ActiveCharter?.CharterId ?? "";
        var expiredAt = Authority.LeaseExpiresAt;

        if (!Authority.QuiesceIfExpired(now)) return false;

        EnqueueUplink(EnvelopeKinds.MoundSync, new
        {
            state = "quiesced",
            charter_id = charterId,
            safe_state = Authority.SafeState,
            lease_expired_at = expiredAt.ToWire()
        }, now);

        return true;
    }

    /// <summary>Stop wins over everything and needs no charter.</summary>
    public void Stop() => Authority.Stop();

    /// <summary>Clear a stop. Restores nothing: the mound waits observe-only for a fresh charter.</summary>
    public void ClearStop() => Authority.ClearStop();

    /// <summary>
    /// Attempt an actuation. Every path produces an ActionRecord carrying its reason — refusals
    /// and clamps are loud, never silent — and every record is queued for the controller.
    ///
    /// <paramref name="requestedOnSeconds"/> is a simulator convenience: when omitted, the sim
    /// fills in the widest duration its own limits allow. The kernel itself never invents a
    /// parameter — a real runtime either supplies one or the request is refused as
    /// <c>missing_parameter</c>.
    /// </summary>
    public ActionRecord Actuate(string capability, DateTimeOffset now, double? requestedOnSeconds = null,
        string missionId = "")
    {
        var request = new CapabilityRequest
        {
            Capability = capability,
            MissionId = missionId,
            Worker = "Forager Ant"
        };

        if (!CapabilityId.IsSense(capability))
            request.Parameters["on_s"] = requestedOnSeconds ?? EffectiveLimits(capability).MaxOnSeconds ?? 1.0;

        return Queue(Kernel.Execute(request, now, new SimEvidenceLookup(_evidence)), now);
    }

    /// <summary>Read a sensor. The reading is the evidence, and is queued as such.</summary>
    public ActionRecord Sense(string capability, DateTimeOffset now, string missionId = "") =>
        Queue(Kernel.Execute(new CapabilityRequest
        {
            Capability = capability,
            MissionId = missionId,
            Worker = "Scout Ant",
            WorkerCeiling = ActionClass.Observe
        }, now, new SimEvidenceLookup(_evidence)), now);

    /// <summary>The bound actually enforced for a capability: hardware ∩ device ∩ charter.</summary>
    public CapabilityLimits EffectiveLimits(string capability)
    {
        var hardware = Kernel.Capabilities.TryGet(capability, out var descriptor)
            ? descriptor.HardwareLimits
            : new CapabilityLimits();

        return LimitClamp.Effective(hardware, Authority.DeviceLimitsFor(capability),
            Authority.CharterLimitsFor(capability));
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

    /// <summary>Evidence the mound has produced, by id — what the controller resolves refs against.</summary>
    public IReadOnlyDictionary<string, EvidenceItem> Evidence => _evidence;

    /// <summary>
    /// SAFETY.md: "every refusal, clamp, trip, and validation failure is reported and audited" —
    /// so a refusal queues its record exactly like a success does.
    /// </summary>
    private ActionRecord Queue(ActionRecord record, DateTimeOffset now)
    {
        EnqueueUplink(EnvelopeKinds.ActionRecord, record, now);
        return record;
    }

    /// <summary>Called by the fake driver so produced evidence stays resolvable afterwards.</summary>
    private void Publish(EvidenceItem item, DateTimeOffset now)
    {
        _evidence[item.EvidenceId] = item;
        EnqueueUplink(EnvelopeKinds.EvidenceBundle,
            new EvidenceBundle { BundleId = Guid.NewGuid().ToString(), Items = [item] }, now);
    }

    /// <summary>
    /// Fake hardware. Observes the actuation it just performed — unless
    /// <see cref="SensorHealthy"/> is false, in which case the work happens and nothing sees it.
    /// </summary>
    private sealed class SimExecutor(string capabilityId, SimMound mound) : ICapabilityExecutor
    {
        public string CapabilityId { get; } = capabilityId;

        public bool IsAvailable => true;

        public ExecutionOutcome Execute(CapabilityExecution execution)
        {
            if (!mound.SensorHealthy) return ExecutionOutcome.Ok();

            var onSeconds = execution.Parameters.TryGetValue("on_s", out var value) ? value : 0;

            var item = new EvidenceItem
            {
                EvidenceId = Guid.NewGuid().ToString(),
                Type = "sensor_window",
                CapturedAt = execution.StartedAt.ToWire(),
                Source = "sim." + CapabilityId,
                PayloadJson =
                    $$"""{"before":0,"after":1,"on_s":{{onSeconds.ToString(CultureInfo.InvariantCulture)}}}"""
            };

            mound.Publish(item, execution.StartedAt);
            return ExecutionOutcome.Ok([item]);
        }
    }

    private sealed class SimEvidenceLookup(IReadOnlyDictionary<string, EvidenceItem> items) : IEvidenceLookup
    {
        public bool TryGet(string evidenceId, out EvidenceItem item)
        {
            if (items.TryGetValue(evidenceId, out var found))
            {
                item = found;
                return true;
            }

            item = new EvidenceItem();
            return false;
        }
    }
}
