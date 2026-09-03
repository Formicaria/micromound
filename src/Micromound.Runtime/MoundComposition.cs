using Micromound.Capabilities;
using Micromound.Evidence;
using Micromound.Protocol;
using Micromound.Sync;

namespace Micromound.Runtime;

/// <summary>
/// The parts a composed mound exposes to its composition root — the kernel that is the one
/// authority boundary, the coordinator, the transport ant, the persistence ant, the watchdog, the
/// evidence store, and the durable queue. Plus <see cref="PublishEvidence"/>: the sink a driver's
/// readings flow into, which the root wires onto its evidence-source drivers after building.
/// </summary>
public sealed record ComposedMound(
    CapabilityKernel Kernel,
    MoundMajor Major,
    RunnerAnt Runner,
    CacheAnt Cache,
    GuardAnt Guard,
    IEvidenceStore EvidenceStore,
    IUplinkQueue Queue,
    Action<EvidenceItem> PublishEvidence)
{
    /// <summary>
    /// Execute a mission and publish its terminal report, then clear the in-flight checkpoint — in
    /// that order. Defined once, here, so every composition root (simulator and host) shares the
    /// exact report-before-clear ordering: the durable result is queued before the intent is
    /// dropped, so a crash between the two re-reports rather than losing the record. The caller
    /// still owns driving hardware to safe state around the call (it holds the drivers) and
    /// persisting authority after.
    /// </summary>
    public MissionReport RunAndReport(Mission mission, DateTimeOffset now)
    {
        var report = Major.Execute(mission, now);
        Runner.Publish(EnvelopeKinds.MissionReport, report, now);   // durable result first
        Major.ClearMissionCheckpoint();                             // then clear the intent
        return report;
    }

    /// <summary>
    /// Recover a mission a restart found unfinished and publish the recovery report, then clear the
    /// checkpoint — same report-before-clear ordering as <see cref="RunAndReport"/>, over the one
    /// shared, fail-closed recovery decision (<see cref="MoundMajor.RecoverMission"/>, which never
    /// replays an ambiguous actuation). The caller de-energizes to safe state before calling this.
    /// </summary>
    public MissionReport RecoverAndReport(MissionCheckpoint checkpoint, DateTimeOffset now)
    {
        var report = Major.RecoverMission(checkpoint, now);
        Runner.Publish(EnvelopeKinds.MissionReport, report, now);   // durable result first
        Major.ClearMissionCheckpoint();                             // then clear the intent
        return report;
    }
}

/// <summary>
/// The one place a Micromound is wired together — <c>capabilities → kernel → ants → Mound Major →
/// Runner</c> — so the simulator and the real host compose the identical runtime and cannot drift.
/// It takes the driver layer's output as capability descriptors and executors (not drivers), and
/// the crypto as signer/verifier interfaces, so this sits in the runtime with no dependency on any
/// concrete driver or crypto implementation: the composition root supplies those.
///
/// <para>The build order is deliberate and matches ARCHITECTURE.md: the kernel (the only thing that
/// holds executors) exists before anything that could move hardware, and the Runner (the only
/// outward-facing worker) is last. The evidence sink it returns closes over the store and the
/// Runner, so a reading reaches the local store and rides out on a bundle with its pressure counts;
/// the root wires that sink onto each evidence-source driver once building is done.</para>
/// </summary>
public static class MoundComposition
{
    public static ComposedMound Build(
        string moundId,
        IReadOnlyList<CapabilityDescriptor> capabilities,
        IReadOnlyList<ICapabilityExecutor> executors,
        IStateStore store,
        IEnvelopeSigner signer,
        IEnvelopeVerifier verifier,
        ISyncTransport transport,
        double guardHeartbeatTimeoutSeconds = 0,
        int evidenceCapacity = 2000,
        int? evidenceHardCeiling = null,
        IEvidenceStore? evidenceStore = null,
        double heartbeatEvidenceIntervalSeconds = 60)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moundId);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(executors);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(transport);

        // Registries and kernel from what the drivers actually expose.
        var capabilityRegistry = new CapabilityRegistry();
        foreach (var descriptor in capabilities)
            capabilityRegistry.Register(descriptor);

        var routines = new RoutineRegistry(capabilityRegistry);
        var kernel = new CapabilityKernel(capabilityRegistry, routines, new KernelAuthority(moundId));

        foreach (var executor in executors)
            kernel.RegisterExecutor(executor);

        // Evidence, then the ants, then the coordinator, then transport — the host's own order.
        // The evidence store is injectable so the host can run the durable file-backed one over real
        // disk (FileEvidenceStore) while the simulator and tests keep the in-memory store; the retention
        // policy is identical either way, only the substrate differs.
        evidenceStore ??= new InMemoryEvidenceStore(evidenceCapacity, evidenceHardCeiling);
        var queue = new DurableUplinkQueue(store);
        var cache = new CacheAnt(store);

        // The guard's health evidence rides the evidence sink defined below, so a watchdog that forced
        // safe state can prove afterwards why — SAFETY.md forbids a silent stop. The sink needs the
        // Runner (built after), so the guard publishes through this holder, assigned once it exists.
        Action<EvidenceItem>? guardSink = null;
        var guard = new GuardAnt(guardHeartbeatTimeoutSeconds, item => guardSink?.Invoke(item), heartbeatEvidenceIntervalSeconds);

        // The Runner is built after the Major, but the Major's action-record sink and the evidence
        // sink both publish through the Runner — so both close over this holder, assigned below.
        RunnerAnt runner = null!;

        var major = new MoundMajor(kernel, evidenceStore,
            recorded: record => runner.Publish(EnvelopeKinds.ActionRecord, record, ParseOrNow(record.EndedAt)));

        runner = new RunnerAnt(major, queue, transport, signer, verifier, evidenceStore);

        void PublishEvidence(EvidenceItem item)
        {
            evidenceStore.Add(item);   // may evict acked or spill unacked under storage pressure

            // The pressure accounting rides out with the bundle it caused — this is where the store
            // meets the wire. Both counts reset on read, so each loss is reported exactly once.
            runner.Publish(EnvelopeKinds.EvidenceBundle,
                new EvidenceBundle
                {
                    BundleId = Guid.NewGuid().ToString(),
                    Items = [item],
                    EvictedAckedItems = evidenceStore.TakeEvictedCount(),
                    SpilledUnackedItems = evidenceStore.TakeSpilledCount()
                },
                ParseOrNow(item.CapturedAt));
        }

        guardSink = PublishEvidence;   // the guard's health readings now reach the store and the wire

        major.Workers.Register(new ScoutAnt(kernel, evidenceStore));
        major.Workers.Register(new ForagerAnt(kernel, evidenceStore));
        major.Workers.Register(guard);
        major.Workers.Register(new WitnessAnt(new EvidenceCorrelator(evidenceStore)));
        major.Workers.Register(cache);
        major.Workers.Register(runner);

        return new ComposedMound(kernel, major, runner, cache, guard, evidenceStore, queue, PublishEvidence);
    }

    private static DateTimeOffset ParseOrNow(string wire) =>
        ProtocolTime.TryParse(wire, out var parsed) ? parsed : DateTimeOffset.UtcNow;
}
