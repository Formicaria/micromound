using Micromound.Capabilities;
using Micromound.Crypto;
using Micromound.Drivers;
using Micromound.Evidence;
using Micromound.Protocol;
using Micromound.Runtime;
using Micromound.Sync;

namespace Micromound.Sim;

/// <summary>
/// An in-memory Micromound, composed the way a Pi will be composed:
///
///     drivers → capability registry → kernel → ants → Mound Major → Runner → controller
///
/// Nothing here reimplements a rule. The drivers are real <see cref="IDriver"/> implementations
/// over fake hardware, the kernel is the real kernel, the ants are the runtime's own six, and the
/// uplink queue is the durable queue persisted through the same <see cref="IStateStore"/> a Pi
/// will back with files. If the simulator and the runtime could disagree, the simulator would be
/// proving nothing.
///
/// Network-free: the "wire" is <see cref="SimLink"/>, an in-process call with an Online switch.
/// </summary>
public sealed class SimMound(string moundId, string tier = SimMound.TierMoundMajor)
{
    /// <summary>Pi-class: full runtime, full envelope set, a Mound Major coordinating ants.</summary>
    public const string TierMoundMajor = "mound_major";

    /// <summary>ESP32-class: compiled routines, reduced envelope set, no open-ended planning.</summary>
    public const string TierController = "deterministic_controller";

    private readonly Dictionary<string, EvidenceItem> _evidenceMirror = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SimDriverBase> _drivers = new(StringComparer.Ordinal);
    private readonly SwitchableTransport _transport = new();
    private readonly InMemoryPublicKeyDirectory _downlinkKeys = new();

    private bool _built;
    private bool _sensorHealthy = true;
    private CapabilityKernel? _kernel;
    private InMemoryEvidenceStore? _evidenceStore;
    private MoundMajor? _major;
    private RunnerAnt? _runner;
    private CacheAnt? _cache;
    private GuardAnt? _guard;
    private DurableUplinkQueue? _queue;

    public string MoundId { get; } = moundId;

    public string Tier { get; } = tier;

    public bool IsReducedProfile => Tier == TierController;

    /// <summary>
    /// The device identity — generated on-device, private half never leaves (PROTOCOL.md §3).
    /// Settable at construction so tests can pin a deterministic key.
    /// </summary>
    public Ed25519KeyPair Keys { get; init; } = Ed25519KeyPair.Generate();

    public byte[] PublicKey => Keys.PublicKey;

    /// <summary>
    /// Operational persistence — what survives a restart. Share one store between two SimMound
    /// instances to simulate a power cycle: build the second with the same store and the same
    /// keys, then call <see cref="Restore"/>.
    /// </summary>
    public IStateStore Store { get; init; } = new InMemoryStateStore();

    /// <summary>Fixed hardware truth for the simulated device.</summary>
    public IReadOnlySet<string> DeviceCapabilities { get; init; } =
        new HashSet<string>(StringComparer.Ordinal) { "sense.temp", "act.relay_1" };

    /// <summary>
    /// Firmware-compiled limits — the innermost tier a charter can only narrow (SAFETY.md Layer 1).
    /// These become the relay drivers' compiled hardware limits.
    /// </summary>
    public Dictionary<string, CapabilityLimits> FirmwareLimits { get; init; } = new(StringComparer.Ordinal)
    {
        ["act.relay_1"] = new CapabilityLimits { MaxOnSeconds = 30, MinOffSeconds = 300, MaxRatePerHour = 6 }
    };

    /// <summary>
    /// Flip to false to simulate dead instrumentation: actuation still happens, nothing observes
    /// it, so records come back `unverified`. Commands are not evidence.
    /// </summary>
    public bool SensorHealthy
    {
        get => _sensorHealthy;
        set
        {
            _sensorHealthy = value;
            foreach (var driver in _drivers.Values) driver.ProduceEvidence = value;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Composition
    // ---------------------------------------------------------------------------------------

    /// <summary>The one physical authority boundary. The simulator holds no other path to hardware.</summary>
    public CapabilityKernel Kernel
    {
        get { EnsureBuilt(); return _kernel!; }
    }

    /// <summary>The authority state the kernel consults — charter, lease, stop, device limits.</summary>
    public KernelAuthority Authority => Kernel.Authority;

    /// <summary>The coordinator, with all six default ants registered.</summary>
    public MoundMajor Major
    {
        get { EnsureBuilt(); return _major!; }
    }

    public RunnerAnt Runner
    {
        get { EnsureBuilt(); return _runner!; }
    }

    public CacheAnt Cache
    {
        get { EnsureBuilt(); return _cache!; }
    }

    public GuardAnt Guard
    {
        get { EnsureBuilt(); return _guard!; }
    }

    /// <summary>The simulated device behind a capability — for tests that move the fake world.</summary>
    public SimSensorDriver Sensor(string capability)
    {
        EnsureBuilt();
        return (SimSensorDriver)_drivers[capability];
    }

    public SimRelayDriver Relay(string capability)
    {
        EnsureBuilt();
        return (SimRelayDriver)_drivers[capability];
    }

    /// <summary>Evidence the mound has produced, by id — what a harness resolves refs against.</summary>
    public IReadOnlyDictionary<string, EvidenceItem> Evidence => _evidenceMirror;

    /// <summary>The retention-governed store behind the Witness — for asserting on ack-driven eviction.</summary>
    public InMemoryEvidenceStore EvidenceStore
    {
        get { EnsureBuilt(); return _evidenceStore!; }
    }

    private void EnsureBuilt()
    {
        if (_built) return;
        _built = true;

        // Drivers first: hardware truth comes from them, not from any document.
        var registry = new DriverRegistry();
        foreach (var id in DeviceCapabilities)
        {
            SimDriverBase driver = CapabilityId.IsSense(id)
                ? new SimSensorDriver(id)
                : new SimRelayDriver(id, FirmwareLimits.TryGetValue(id, out var limits) ? limits : null);

            driver.ProduceEvidence = _sensorHealthy;
            driver.Publish = PublishEvidence;
            _drivers[id] = driver;
            registry.Register(driver);
        }

        // Registries and kernel from what the drivers actually expose.
        var capabilities = new CapabilityRegistry();
        foreach (var driver in registry.All)
            foreach (var descriptor in driver.Capabilities)
                capabilities.Register(descriptor);

        var routines = new RoutineRegistry(capabilities);
        _kernel = new CapabilityKernel(capabilities, routines, new KernelAuthority(MoundId));

        foreach (var driver in registry.All)
            foreach (var executor in driver.Executors)
                _kernel.RegisterExecutor(executor);

        // Evidence, then the ants, then the coordinator, then transport — the host's own order.
        _evidenceStore = new InMemoryEvidenceStore();
        _queue = new DurableUplinkQueue(Store);
        _cache = new CacheAnt(Store);
        _guard = new GuardAnt(heartbeatTimeoutSeconds: 0);   // liveness is the harness's, explicitly

        _major = new MoundMajor(_kernel, _evidenceStore,
            recorded: record => _runner!.Publish(EnvelopeKinds.ActionRecord, record, ParseOrNow(record.EndedAt)));

        _runner = new RunnerAnt(_major, _queue, _transport,
            new Ed25519EnvelopeSigner(MoundId, Keys),
            new Ed25519EnvelopeVerifier(_downlinkKeys),
            _evidenceStore);

        _major.Workers.Register(new ScoutAnt(_kernel, _evidenceStore));
        _major.Workers.Register(new ForagerAnt(_kernel, _evidenceStore));
        _major.Workers.Register(_guard);
        _major.Workers.Register(new WitnessAnt(new EvidenceCorrelator(_evidenceStore)));
        _major.Workers.Register(_cache);
        _major.Workers.Register(_runner);
    }

    private void PublishEvidence(EvidenceItem item)
    {
        _evidenceMirror[item.EvidenceId] = item;
        _evidenceStore!.Add(item);   // may evict acked or spill unacked under storage pressure

        // The pressure accounting rides out with the bundle it caused — the composition root is
        // where the store meets the wire, so it is where the counts are attached. Both reset on
        // read, so each loss is reported exactly once.
        _runner!.Publish(EnvelopeKinds.EvidenceBundle,
            new EvidenceBundle
            {
                BundleId = Guid.NewGuid().ToString(),
                Items = [item],
                EvictedAckedItems = _evidenceStore.TakeEvictedCount(),
                SpilledUnackedItems = _evidenceStore.TakeSpilledCount()
            },
            ParseOrNow(item.CapturedAt));
    }

    private static DateTimeOffset ParseOrNow(string wire) =>
        ProtocolTime.TryParse(wire, out var parsed) ? parsed : DateTimeOffset.UtcNow;

    // ---------------------------------------------------------------------------------------
    // The controller link
    // ---------------------------------------------------------------------------------------

    /// <summary>The wire to the controller. Null until <see cref="ConnectTo"/>.</summary>
    public SimLink? Link { get; private set; }

    /// <summary>
    /// Enroll with a controller and hold its link — PROTOCOL.md §3 compressed to its effect:
    /// the controller learns this device's public key, this device learns the controller's, and
    /// from here on only signed traffic crosses in either direction.
    /// </summary>
    public SimLink ConnectTo(SimController controller)
    {
        EnsureBuilt();

        var controllerKey = controller.Enroll(MoundId, PublicKey);
        _downlinkKeys.Register(KeyIds.Controller, controllerKey);

        Link = new SimLink(controller);
        _transport.Inner = Link;
        return Link;
    }

    /// <summary>
    /// Swap the wire under the Runner — the test seam a man-in-the-middle scenario needs. Null
    /// disconnects. Everything above the transport is untouched, which is the point: tampering
    /// tests prove the endpoints, not the harness.
    /// </summary>
    public void UseTransport(ISyncTransport? transport)
    {
        EnsureBuilt();
        _transport.Inner = transport;
    }

    /// <summary>One sync beat: drain the backlog, handle the downlink, persist what changed.</summary>
    public SyncOutcome Sync(DateTimeOffset now)
    {
        var outcome = WatchingForSafeState(() => Runner.Sync(now));
        Cache.SaveAuthority(Authority);   // a beat can renew, charter, stop, or quiesce this mound
        return outcome;
    }

    /// <summary>Execute a mission locally and queue its report — what a downlinked mission also does.</summary>
    public MissionReport ExecuteMission(Mission mission, DateTimeOffset now)
    {
        var report = WatchingForSafeState(() => Major.Execute(mission, now));
        Runner.Publish(EnvelopeKinds.MissionReport, report, now);
        Cache.SaveAuthority(Authority);
        return report;
    }

    /// <summary>
    /// A stop or a quiesce can happen INSIDE the wrapped call — a stop order in the downlink, a
    /// guard trip mid-mission, a lease found expired — and PROTOCOL.md §7's "enter safe_state"
    /// means the drivers, not just the authority flag. The composition root owns the drivers, so
    /// the composition root watches for the transition; the M4 host will do exactly this around
    /// its own loop.
    /// </summary>
    private T WatchingForSafeState<T>(Func<T> action)
    {
        var wasSafe = Authority.IsStopped || Authority.IsQuiesced;
        var result = action();

        if (!wasSafe && (Authority.IsStopped || Authority.IsQuiesced))
            foreach (var driver in _drivers.Values)
                driver.EnterSafeState();

        return result;
    }

    /// <summary>
    /// Rehydrate persisted authority after a restart. All downward-resolving rules live in
    /// <see cref="KernelAuthority.Restore"/>: a restart never clears a stop, never extends a
    /// lease, and restores observe-only when in doubt.
    /// </summary>
    public ValidationResult Restore(DateTimeOffset now)
    {
        EnsureBuilt();
        Cache.TryRestoreAuthority(Authority, now, out var result,
            Kernel.Capabilities.DeclaredCapabilities(), Kernel.Routines.DeclaredRoutines());

        // Authority restored first — a stop or an expired lease is now in force if it was — so the
        // mission recovery reads the same authority a fresh mission would. A mission the last run
        // never finished left a checkpoint; the Mound Major decides its outcome deterministically
        // (it never replays physical work) and the report is queued for the controller, so the
        // audit trail stays whole across the restart. The M4 host runs this around its own loop, and
        // additionally de-energizes drivers to the checkpoint's safe_state on a cold start.
        //
        // Durability ordering, for the M4 host with a real (disk) state store: the terminal report
        // is the mission's RESULT, and by the same intent->execute->result discipline the checkpoint
        // (the INTENT) must be cleared only AFTER that report is durably persisted. Here the store is
        // in-memory, so the order of the two is immaterial and RecoverMission clears the checkpoint
        // itself via Finish. On a durable store the host must instead persist the report first and
        // clear the checkpoint after, so that a crash between the two re-reports on the next restart
        // rather than losing the terminal report — the audit-record analogue of the no-replay rule.
        if (Cache.TryLoad<MissionCheckpoint>(MissionCheckpoint.Key, out var checkpoint))
        {
            var report = Major.RecoverMission(checkpoint, now);   // clears the checkpoint via Finish
            Runner.Publish(EnvelopeKinds.MissionReport, report, now);
        }

        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Authority — thin passthroughs, each persisting the state it changed
    // ---------------------------------------------------------------------------------------

    public string State => Authority.State;

    public bool LeaseAlive(DateTimeOffset now) => Authority.LeaseAlive(now);

    /// <summary>Downlink: the controller offers a charter. Invalid charters are refused, state untouched.</summary>
    public ValidationResult OfferCharter(Charter charter, DateTimeOffset now)
    {
        var result = Major.AcceptCharter(charter, now);
        if (result.IsValid) Cache.SaveAuthority(Authority);
        return result;
    }

    /// <summary>Apply a declarative configuration manifest — the middle limit tier and the safe state.</summary>
    public ValidationResult ApplyManifest(MoundManifest manifest)
    {
        var result = Major.ApplyManifest(manifest, DateTimeOffset.MinValue);
        if (result.IsValid) Cache.SaveAuthority(Authority);
        return result;
    }

    /// <summary>Sync beat acknowledged: the lease renews. Nothing else about authority changes.</summary>
    public void RenewLease(DateTimeOffset now)
    {
        Authority.RenewLease(now);
        Cache.SaveAuthority(Authority);
    }

    /// <summary>
    /// Lease expiry check — call on the device's own clock tick. Expired ⇒ safe state, drivers
    /// told to de-energize, and a `quiesced` report queued for the controller (PROTOCOL.md §5).
    /// </summary>
    public bool QuiesceIfExpired(DateTimeOffset now)
    {
        var charterId = Authority.ActiveCharter?.CharterId ?? "";
        var expiredAt = Authority.LeaseExpiresAt;

        if (!Authority.QuiesceIfExpired(now)) return false;

        foreach (var driver in _drivers.Values) driver.EnterSafeState();

        Runner.Publish(EnvelopeKinds.MoundSync, new
        {
            state = "quiesced",
            charter_id = charterId,
            safe_state = Authority.SafeState,
            lease_expired_at = expiredAt.ToWire()
        }, now);

        Cache.SaveAuthority(Authority);
        return true;
    }

    /// <summary>Stop wins over everything and needs no charter. Drivers enter their passive state.</summary>
    public void Stop()
    {
        Major.Stop();
        foreach (var driver in _drivers.Values) driver.EnterSafeState();
        Cache.SaveAuthority(Authority);
    }

    /// <summary>Clear a stop. Restores nothing: the mound waits observe-only for a fresh charter.</summary>
    public void ClearStop()
    {
        Authority.ClearStop();
        Cache.SaveAuthority(Authority);
    }

    // ---------------------------------------------------------------------------------------
    // Direct actuation and sensing — the ants, not a shortcut around them
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Attempt an actuation through the Forager Ant. Every path produces an ActionRecord carrying
    /// its reason — refusals and clamps are loud, never silent — and every record is queued for
    /// the controller.
    ///
    /// <paramref name="requestedOnSeconds"/> is a simulator convenience: when omitted, the sim
    /// fills in the widest duration its own limits allow. The kernel itself never invents a
    /// parameter — a real runtime either supplies one or the request is refused as
    /// <c>missing_parameter</c>.
    /// </summary>
    public ActionRecord Actuate(string capability, DateTimeOffset now, double? requestedOnSeconds = null,
        string missionId = "")
    {
        EnsureBuilt();

        var request = new CapabilityRequest { Capability = capability, MissionId = missionId };
        if (!CapabilityId.IsSense(capability))
            request.Parameters["on_s"] = requestedOnSeconds ?? EffectiveLimits(capability).MaxOnSeconds ?? 1.0;

        var forager = _major!.Workers.All.OfType<ForagerAnt>().First();
        return Queue(forager.Request(request, now), now);
    }

    /// <summary>Read a sensor through the Scout Ant. The reading is the evidence, and is queued as such.</summary>
    public ActionRecord Sense(string capability, DateTimeOffset now, string missionId = "")
    {
        EnsureBuilt();

        var scout = _major!.Workers.All.OfType<ScoutAnt>().First();
        return Queue(scout.Sense(new CapabilityRequest { Capability = capability, MissionId = missionId }, now), now);
    }

    /// <summary>The bound actually enforced for a capability: hardware ∩ device ∩ charter.</summary>
    public CapabilityLimits EffectiveLimits(string capability)
    {
        var hardware = Kernel.Capabilities.TryGet(capability, out var descriptor)
            ? descriptor.HardwareLimits
            : new CapabilityLimits();

        return LimitClamp.Effective(hardware, Authority.DeviceLimitsFor(capability),
            Authority.CharterLimitsFor(capability));
    }

    // ---------------------------------------------------------------------------------------
    // Uplink
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Queue an uplink envelope, signed and chained (works offline). The Runner Ant is the only
    /// envelope factory on this mound; this is a named door to it.
    /// </summary>
    public Envelope EnqueueUplink<T>(string kind, T body, DateTimeOffset now) =>
        Runner.Publish(kind, body, now);

    /// <summary>
    /// Drain queued uplink as a harness playing controller: oldest first, chain intact, and
    /// acknowledged on the way out — which is exactly what a real controller's ack does.
    /// </summary>
    public IReadOnlyList<Envelope> DrainUplink()
    {
        EnsureBuilt();

        var drained = _queue!.Peek(int.MaxValue);
        if (drained.Count > 0) _queue.AcknowledgeThrough(drained[^1].Seq);
        return drained;
    }

    /// <summary>
    /// SAFETY.md: "every refusal, clamp, trip, and validation failure is reported and audited" —
    /// so a refusal queues its record exactly like a success does.
    /// </summary>
    private ActionRecord Queue(ActionRecord record, DateTimeOffset now)
    {
        Runner.Publish(EnvelopeKinds.ActionRecord, record, now);
        return record;
    }

    /// <summary>A transport that can be connected after composition. Unconnected means offline.</summary>
    private sealed class SwitchableTransport : ISyncTransport
    {
        public ISyncTransport? Inner { get; set; }

        public bool TryExchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail)
        {
            if (Inner is not null) return Inner.TryExchange(uplink, out downlink, out detail);

            downlink = [];
            detail = "no controller connected";
            return false;
        }
    }
}
