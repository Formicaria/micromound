using Micromound.Capabilities;
using Micromound.Crypto;
using Micromound.Drivers;
using Micromound.Protocol;
using Micromound.Runtime;
using Micromound.Sync;

namespace Micromound.Host;

/// <summary>Raised when a mound cannot come up: bad manifest, unresolvable drivers, missing safe state.</summary>
public sealed class HostStartupException(string message) : Exception(message);

/// <summary>What a host needs to bring a mound up. Identity and manifest are the operator's; the
/// rest have safe defaults so a test or a first bring-up need only supply those two and a directory.</summary>
public sealed class HostOptions
{
    /// <summary>The device identity — private half never leaves the device. See <see cref="MoundHost.LoadOrCreateIdentity"/>.</summary>
    public required Ed25519KeyPair Keys { get; init; }

    /// <summary>The validated hardware/authority manifest this mound runs.</summary>
    public required MoundManifest Manifest { get; init; }

    /// <summary>State root: durable state lives in <c>state/</c> beneath it, identity in <c>identity/</c>.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>Driver types this build can instantiate. Defaults to the generic primitives.</summary>
    public DriverFactoryRegistry? Drivers { get; init; }

    /// <summary>The link to the controller. Offline by default — the host runs and the queue drains later.</summary>
    public ISyncTransport? Transport { get; init; }

    /// <summary>The controller public key(s) downlink is verified against. Empty by default (offline bring-up).</summary>
    public IPublicKeyDirectory? ControllerKeys { get; init; }

    /// <summary>Watchdog heartbeat timeout in seconds; 0 disables the timing check (the caller drives liveness).</summary>
    public double GuardHeartbeatTimeoutSeconds { get; init; }
}

/// <summary>
/// The real composition root — the headless daemon's runtime, built from a manifest over a durable
/// file-backed store, using the exact <see cref="MoundComposition"/> the simulator uses so the two
/// cannot drift. This slice makes the mound composable and runnable from a manifest and disk; the
/// OS service loop, signal handling, and a network transport are a following slice, which is why the
/// transport is injected and defaults to offline.
///
/// <para>Bring-up fails closed: an unresolvable driver, a malformed manifest, or a missing safe state
/// throws rather than leaving a half-configured mound that could move hardware it does not understand.</para>
/// </summary>
public sealed class MoundHost
{
    private readonly IReadOnlyList<IDriver> _drivers;
    private readonly ComposedMound _mound;
    private readonly byte[] _publicKey;

    private MoundHost(string moundId, byte[] publicKey, IReadOnlyList<IDriver> drivers, ComposedMound mound)
    {
        MoundId = moundId;
        _publicKey = publicKey;
        _drivers = drivers;
        _mound = mound;
    }

    public string MoundId { get; }
    public string State => _mound.Kernel.Authority.State;
    public KernelAuthority Authority => _mound.Kernel.Authority;
    public CapabilityKernel Kernel => _mound.Kernel;
    public MoundMajor Major => _mound.Major;
    public RunnerAnt Runner => _mound.Runner;
    public CacheAnt Cache => _mound.Cache;

    /// <summary>The device's public key — safe to publish; the controller enrolls it.</summary>
    public byte[] PublicKey => (byte[])_publicKey.Clone();

    /// <summary>
    /// Bring a mound up from a manifest over a durable file store. Fails closed: on any composition
    /// error nothing is returned and no drivers are left initialized.
    /// </summary>
    public static MoundHost Create(HostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Keys);
        ArgumentNullException.ThrowIfNull(options.Manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateDirectory);

        var manifest = options.Manifest;
        var moundId = manifest.MoundId;
        if (string.IsNullOrWhiteSpace(moundId))
            throw new HostStartupException("manifest has no mound_id");
        if (string.IsNullOrWhiteSpace(manifest.SafeState))
            throw new HostStartupException("manifest declares no safe_state; a mound must know how to de-energize");

        // Drivers from the manifest's hardware, fail-closed as a whole.
        var factories = options.Drivers ?? DefaultDriverFactories();
        var resolution = ManifestDriverComposer.Compose(manifest, factories);
        if (!resolution.IsValid)
            throw new HostStartupException(
                "hardware could not be resolved from the manifest: " + string.Join("; ", resolution.Result.Errors));

        // The drivers are now configured — for real hardware that means lines are claimed. If any
        // later step of bring-up fails, drive them all to safe state before rethrowing, so a failed
        // start never leaves hardware energized or half-claimed. (A Dispose seam arrives with the
        // real ports; EnterSafeState is the teardown available now.)
        try
        {
            var store = new FileStateStore(Path.Combine(options.StateDirectory, "state"));

            var composed = MoundComposition.Build(
                moundId,
                resolution.Drivers.SelectMany(d => d.Capabilities).ToList(),
                resolution.Drivers.SelectMany(d => d.Executors).ToList(),
                store,
                new Ed25519EnvelopeSigner(moundId, options.Keys),
                new Ed25519EnvelopeVerifier(options.ControllerKeys ?? new InMemoryPublicKeyDirectory()),
                options.Transport ?? new OfflineTransport(),
                options.GuardHeartbeatTimeoutSeconds);

            // Wire each evidence-source driver's readings into the shared sink.
            foreach (var driver in resolution.Drivers)
                if (driver is IEvidenceSource source)
                    source.Publish = composed.PublishEvidence;

            // Apply the manifest's authority slice (device_limits, declared capabilities, safe_state).
            var applied = composed.Major.ApplyManifest(manifest, DateTimeOffset.MinValue);
            if (!applied.IsValid)
                throw new HostStartupException("manifest refused: " + string.Join("; ", applied.Errors));

            return new MoundHost(moundId, options.Keys.PublicKey, resolution.Drivers, composed);
        }
        catch
        {
            foreach (var driver in resolution.Drivers)
                try { driver.EnterSafeState(); } catch { /* one driver must not stop the others */ }
            throw;
        }
    }

    /// <summary>The generic driver primitives every build ships. Real hardware ports register more.</summary>
    public static DriverFactoryRegistry DefaultDriverFactories()
    {
        var factories = new DriverFactoryRegistry();
        factories.Register(new AnalogSensorFactory());
        factories.Register(new DigitalActuatorFactory());
        return factories;
    }

    /// <summary>
    /// Rehydrate persisted authority after a restart, then recover any mission the last run never
    /// finished — the same deterministic, fail-closed decision the simulator runs, driven here over
    /// the real file store. A cold start with a mission in flight de-energizes to safe state first,
    /// and the recovery report is queued before its checkpoint is cleared.
    /// </summary>
    public ValidationResult Restore(DateTimeOffset now)
    {
        Cache.TryRestoreAuthority(Authority, now, out var result,
            Kernel.Capabilities.DeclaredCapabilities(), Kernel.Routines.DeclaredRoutines());

        if (Cache.TryLoad<MissionCheckpoint>(MissionCheckpoint.Key, out var checkpoint))
        {
            foreach (var driver in _drivers) driver.EnterSafeState();   // cold-start safe
            _mound.RecoverAndReport(checkpoint, now);                   // shared: recover -> publish -> clear
        }

        return result;
    }

    /// <summary>One sync beat: drain the backlog, handle the downlink, persist what changed.</summary>
    public SyncOutcome Sync(DateTimeOffset now)
    {
        var outcome = WatchingForSafeState(() => Runner.Sync(now));
        Cache.SaveAuthority(Authority);
        return outcome;
    }

    /// <summary>Execute a mission locally and queue its report — what a downlinked mission also does.</summary>
    public MissionReport ExecuteMission(Mission mission, DateTimeOffset now)
    {
        var report = WatchingForSafeState(() => _mound.RunAndReport(mission, now));
        Cache.SaveAuthority(Authority);
        return report;
    }

    /// <summary>
    /// A stop or quiesce can happen inside the wrapped call; when authority crosses into a safe
    /// state, the drivers — which the composition root owns — are driven to their safe state, not
    /// just the authority flag.
    /// </summary>
    private T WatchingForSafeState<T>(Func<T> action)
    {
        var wasSafe = Authority.IsStopped || Authority.IsQuiesced;
        var result = action();
        if (!wasSafe && (Authority.IsStopped || Authority.IsQuiesced))
            foreach (var driver in _drivers) driver.EnterSafeState();
        return result;
    }

    /// <summary>
    /// Load the device identity seed from <c>&lt;stateDirectory&gt;/identity/seed</c>, or generate one
    /// and persist it there on first boot. The private seed never leaves the device — SAFETY.md.
    /// </summary>
    public static Ed25519KeyPair LoadOrCreateIdentity(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var dir = Path.Combine(stateDirectory, "identity");
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best effort; a permissive dir is not worth failing bring-up over */ }

        var seedPath = Path.Combine(dir, "seed");
        if (File.Exists(seedPath))
            return LoadSeed(seedPath);

        var generated = Ed25519KeyPair.Generate();
        var tmp = seedPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteSeedOwnerOnly(tmp, generated.Seed);
            // overwrite:false so a concurrent boot that wrote the seed first is not clobbered.
            File.Move(tmp, seedPath, overwrite: false);
            return generated;
        }
        catch (IOException) when (File.Exists(seedPath))
        {
            // Another boot won the race and persisted the seed: discard ours, load theirs.
            SafeDelete(tmp);
            return LoadSeed(seedPath);
        }
        catch
        {
            SafeDelete(tmp);
            throw;
        }
    }

    private static Ed25519KeyPair LoadSeed(string seedPath)
    {
        var seed = File.ReadAllBytes(seedPath);
        if (seed.Length == Ed25519KeyPair.SeedLength)
            return Ed25519KeyPair.FromSeed(seed);
        throw new HostStartupException(
            $"identity seed at '{seedPath}' is {seed.Length} bytes, expected {Ed25519KeyPair.SeedLength}");
    }

    private static void WriteSeedOwnerOnly(string path, byte[] seed)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // 0600: a signing key
        using var stream = new FileStream(path, options);
        stream.Write(seed);
        stream.Flush();
        stream.Flush(flushToDisk: true);   // the identity must survive a power cut, like the state store
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

/// <summary>
/// The offline transport: reaching the controller always reports offline, which is a normal state,
/// not an error — the durable uplink queue holds the backlog until a real transport (a following
/// slice) can drain it. Lets the host compose and run locally with no network present.
/// </summary>
public sealed class OfflineTransport : ISyncTransport
{
    public bool TryExchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail)
    {
        downlink = [];
        detail = "offline: no controller transport configured";
        return false;
    }
}
