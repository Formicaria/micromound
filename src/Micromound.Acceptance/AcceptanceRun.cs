using System.Reflection;
using System.Text.Json;
using Micromound.Capabilities;
using Micromound.Crypto;
using Micromound.Drivers;
using Micromound.Host;
using Micromound.Protocol;
using Micromound.Runtime;
using Micromound.Sim;
using Micromound.Sync;

namespace Micromound.Acceptance;

/// <summary>
/// The Generic Physical Mound acceptance sequence (docs/ROADMAP.md, "The target"), executed in
/// order against a real mound: a real <see cref="MoundHost"/> composed from a manifest, a real
/// durable file store on disk, real generic drivers, a real signed wire to an in-process controller,
/// and — when the C tools are built — the real port-server firmware in its own process on the other
/// end of a real byte stream.
///
/// <para>Nothing here is a new code path for the sake of the check: every criterion is asserted
/// against the same objects the daemon runs. What the harness supplies is the bench — a controller
/// that enrolls and charters, a clock it advances by hand, and a world in which opening a valve has
/// a consequence a separate sensor can see.</para>
///
/// <para>The criteria are the acceptance prose, split at its semicolons and numbered. A criterion
/// that cannot be met without hardware this leg does not have is reported <c>n/a</c> with the
/// reason, never silently passed.</para>
/// </summary>
public sealed class AcceptanceRun(BoardProcess? board, string stateRoot, Action<string>? trace = null)
{
    public const string MoundId = "mm-acceptance-01";
    private const string Valve = "act.valve";
    private const string Level = "sense.tank_level";
    private const string Closed = "sense.valve_closed";

    private readonly List<Criterion> _criteria = [];
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-06T08:00:00Z");

    private SimController _controller = null!;
    private SimLink _link = null!;
    private Ed25519KeyPair _keys = null!;
    private MoundHost _host = null!;
    private InMemoryPublicKeyDirectory _controllerKeys = null!;
    private MoundManifest _manifest = null!;
    private DateTimeOffset _leaseExpiry;
    private string _actionId = "";

    public IReadOnlyList<Criterion> Criteria => _criteria;
    public bool Passed => _criteria.All(c => c.Verdict != Verdict.Failed);

    /// <summary>The whole sequence. Never throws for a failed criterion — it records it and goes on.</summary>
    public void Run()
    {
        Boots();
        BoardDiscovered();
        HardwareEnumerated();
        Enrolls();
        ConfigurationAccepted();
        CharterAccepted();
        BindsWithoutChangingCode();
        MissionCoordinated();
        KernelValidatedLimits();
        BoundedRequestReachedTheBoard();
        WitnessConfirmedIndependently();
        OutcomeReflectsReality();
        NetworkDropsAndWorkContinuesInsideTheLease();
        RebootRestoresEverything();
        LeaseExpiresIntoTheSafeState();
        ReconnectDoesNotResumeAuthorityAndTheHistorySyncs();
        StopSurvivesARestart();
        TheBoardEnforcesItsOwnTier();
    }

    // ------------------------------------------------------------------ 1

    private void Boots() => Check(1, "a fresh mound boots its unchanged default mini-colony", () =>
    {
        _controller = new SimController();
        _link = new SimLink(_controller);
        _keys = Ed25519KeyPair.Generate();
        _controllerKeys = new InMemoryPublicKeyDirectory();
        _manifest = Manifest();

        Directory.CreateDirectory(StateDirectory);
        _host = MoundHost.Create(new HostOptions
        {
            Keys = _keys,
            Manifest = _manifest,
            StateDirectory = StateDirectory,
            Drivers = Factories(),
            Transport = _link,
            ControllerKeys = _controllerKeys,
            Settle = Settle
        });

        var present = _host.Major.Workers.Names.ToHashSet(StringComparer.Ordinal);
        var expected = DefaultAnts.All.Where(a => a != DefaultAnts.MoundMajor).ToList();
        var missing = expected.Where(a => !present.Contains(a)).ToList();
        if (missing.Count > 0) return Fail("missing: " + string.Join(", ", missing));
        if (_host.State != MoundStates.ObserveOnly) return Fail($"a fresh mound is '{_host.State}', not observe_only");
        return Ok($"Mound Major + {string.Join(", ", expected.Select(Short))}; state observe_only");
    });

    // ------------------------------------------------------------------ 2

    private void BoardDiscovered() => Check(2, "the board is discovered over the link", () =>
    {
        if (board is null) return NotApplicable("this leg runs on in-memory ports; no board is attached");
        var hello = board.Hello();
        if (hello.Tripped) return Fail("the board reports itself tripped");
        if (hello.Pins.Count == 0 || hello.Inputs.Count == 0 || hello.Channels.Count == 0)
            return Fail($"the board offers {hello.Pins.Count} pin(s), {hello.Inputs.Count} input(s), {hello.Channels.Count} channel(s)");
        return Ok($"{hello.Profile} firmware {hello.Firmware}: pin(s) {Join(hello.Pins.Select(p => p.Pin))}, " +
                  $"input(s) {Join(hello.Inputs.Select(p => p.Pin))}, channel(s) {Join(hello.Channels)}, watchdog {hello.WatchdogSeconds}s");
    });

    // ------------------------------------------------------------------ 3

    private void HardwareEnumerated() => Check(3, "hardware is loaded from the manifest and capabilities register", () =>
    {
        var declared = _host.Kernel.Capabilities.DeclaredCapabilities().ToHashSet(StringComparer.Ordinal);
        var wanted = new[] { Valve, Level, Closed };
        var missing = wanted.Where(c => !declared.Contains(c)).ToList();
        if (missing.Count > 0) return Fail("did not register: " + string.Join(", ", missing));
        return Ok($"{_manifest.Hardware.Count} device(s) → {wanted.Length} capabilities: {string.Join(", ", wanted)}");
    });

    // ------------------------------------------------------------------ 4

    private void Enrolls() => Check(4, "the mound enrolls upstream", () =>
    {
        var link = MoundHost.ResolveControllerLink(StateDirectory, new SimEnrollmentClient(_controller), _keys.PublicKey, "tok-acceptance");
        if (!link.Enrolled) return Fail(link.Detail);
        foreach (var id in new[] { KeyIds.Controller })
            if (link.Keys.TryGetPublicKey(id, out var key)) _controllerKeys.Register(id, key);
        if (!File.Exists(Path.Combine(StateDirectory, "controller.pub"))) return Fail("the controller key was not persisted");
        return Ok($"{link.Detail}; the controller key is on disk and downlink now verifies against it");
    });

    // ------------------------------------------------------------------ 5

    private void ConfigurationAccepted() => Check(5, "signed configuration is accepted and persisted", () =>
    {
        var narrowed = Manifest();
        narrowed.ManifestId = "mf-acceptance-2";
        narrowed.DeviceLimits[Valve] = new CapabilityLimits { MaxOnSeconds = 20 };
        _controller.PushConfig(narrowed, _now);
        _host.Sync(_now);

        var applied = _host.Authority.DeviceLimitsFor(Valve);
        if (applied?.MaxOnSeconds is not 20) return Fail($"the device tier is {applied?.MaxOnSeconds?.ToString() ?? "unset"}, not 20");
        _manifest = narrowed;
        return Ok("config downlink verified under the controller key; device tier act.valve max_on_s 20 (checked again after the reboot)");
    });

    // ------------------------------------------------------------------ 6

    private void CharterAccepted() => Check(6, "a signed charter is accepted and persisted", () =>
    {
        _controller.IssueCharter(Charter(), _now);
        _host.Sync(_now);
        if (_host.State != MoundStates.Chartered) return Fail($"the mound is '{_host.State}' after the charter");
        _leaseExpiry = _host.Authority.LeaseExpiresAt;
        return Ok($"chartered; ceiling benign, act.valve max_on_s 10, lease {(int)(_leaseExpiry - _now).TotalSeconds}s, evidence required for act.*");
    });

    // ------------------------------------------------------------------ 7

    private void BindsWithoutChangingCode() => Check(7, "configuration binds generic hardware to the generic ants, without changing their code", () =>
    {
        // The load-bearing property: a device-specific class in the core is the signal an abstraction
        // is wrong (ROADMAP). Nothing in the runtime may be named after what this bench happens to be.
        var appliances = new[] { "greenhouse", "rover", "valve", "pump", "tank", "irrigation", "thermostat", "boiler", "hvac", "aquarium", "kiln", "printer", "drone" };
        var offenders = new List<string>();
        foreach (var assembly in CoreAssemblies())
            foreach (var type in assembly.GetTypes().Where(t => t.IsPublic))
            {
                var name = type.Name.ToLowerInvariant();
                offenders.AddRange(appliances.Where(a => name.Contains(a, StringComparison.Ordinal))
                                             .Select(a => $"{assembly.GetName().Name}.{type.Name} (‘{a}’)"));
            }
        if (offenders.Count > 0) return Fail("device-specific types in the core: " + string.Join(", ", offenders));

        // And the same generic driver types are what this bench's three capabilities actually are.
        var bindings = string.Join(", ", _manifest.Hardware.Select(h => $"{h.Key}={h.Value.Driver}"));
        var types = _manifest.Hardware.Values.Select(h => h.Driver).Distinct().OrderBy(d => d, StringComparer.Ordinal);
        return Ok($"{bindings}; {CoreAssemblies().Count()} core assemblies carry no appliance type; driver types used: {string.Join(", ", types)}");
    });

    // ------------------------------------------------------------------ 8, 9, 10, 11

    private MissionReport _watering = new();

    private void MissionCoordinated() => Check(8, "a mission is coordinated by the Mound Major", () =>
    {
        // The mission's own settle window advances the board mid-walk; nothing here has to.
        _watering = _host.ExecuteMission(Watering("m-1"), _now);
        AdvanceBoard(12);                                     // the hold elapses on the board's clock
        _host.ServiceActuations(_now = _now.AddSeconds(12));   // ...and on the Pi's, which is what releases the line
        Beat();                                               // records and the report ride up on the next beat
        if (_watering.State != MissionStates.Completed) return Fail($"{_watering.State}: {_watering.Detail}");
        var steps = string.Join(" → ", _watering.Steps.Select(s => $"{s.StepId}:{s.State}"));
        return Ok($"{_watering.Steps.Count} steps, {steps}");
    });

    private void KernelValidatedLimits() => Check(9, "the Forager requests actuation and the kernel validates authority and limits", () =>
    {
        var record = _controller.Account(MoundId).Records.FirstOrDefault(r => r.Capability == Valve)
                     ?? _host.Major.Actions.FirstOrDefault(r => r.Capability == Valve);
        if (record is null) return Fail("no actuation record reached the controller");
        _actionId = record.ActionId;
        if (!record.RequestedParameters.TryGetValue("on_s", out var asked) || asked != 60) return Fail($"the mission asked for {asked}s, expected 60");
        if (!record.Parameters.TryGetValue("on_s", out var ran) || ran != 10) return Fail($"the kernel let {ran}s through, expected the charter's 10");
        if (record.Outcome != ActionOutcomes.Clamped) return Fail($"the outcome is '{record.Outcome}', not clamped");
        return Ok($"asked 60s → ran {ran}s; {record.Detail}");
    });

    private void BoundedRequestReachedTheBoard() => Check(10, "a generic driver sends a bounded request to the board and the board acts", () =>
    {
        if (board is null) return NotApplicable("this leg runs on in-memory ports; the driver drove an in-memory line");
        var world = board.World();
        var pin = world.Pin(5);
        if (pin.Writes < 2) return Fail($"the board saw {pin.Writes} write(s); an actuation is a drive and a release");
        if (pin.Active) return Fail("the line is still active after the hold elapsed");
        return Ok($"pin 5 driven and released, {pin.Writes} write(s), the line is at its safe level, board clock {world.Now}s");
    });

    private void WitnessConfirmedIndependently() => Check(11, "the Witness confirms with independent evidence", () =>
    {
        // Since `v0.9.30` the mission asserts what the switch must read, so this criterion tests
        // agreement rather than mere presence. Criterion 12 below is its mirror: the same mission,
        // with the witness blinded, must NOT confirm.

        var verify = _watering.Steps.FirstOrDefault(s => s.StepId == "confirm");
        if (verify is null) return Fail("the mission ran no verify step");
        if (verify.State != MissionStepStates.Executed) return Fail($"the verify step is {verify.State}: {verify.Detail}");
        if (verify.Value is not 1) return Fail($"the limit switch read {verify.Value?.ToString() ?? "nothing"}, not 1 — nothing independent saw the valve move");
        var record = Records().FirstOrDefault(r => r.ActionId == _actionId);
        if (record is null) return Fail("the confirmed record never reached the controller");
        if (!ActionOutcomes.AssertPhysicalWork.Contains(record.Outcome)) return Fail($"the confirmed action reads '{record.Outcome}'");
        var source = Evidence().Values.FirstOrDefault(e => e.EvidenceId == verify.EvidenceRefs.FirstOrDefault())?.Source ?? "";
        return Ok($"a separate {Closed} line read 1 after the act and SATISFIED the mission's postcondition " +
                  $"(eq 1 closed); the record stands '{record.Outcome}'" + (source.Length > 0 ? $" (source {source})" : ""));
    });

    // ------------------------------------------------------------------ 12

    private void OutcomeReflectsReality() => Check(12, "the result reflects verified / unverified / failed reality", () =>
    {
        // Break the witness — not the valve. The actuation still happens; nothing independent can see
        // it; the record must say so rather than claim success.
        if (board is not null) board.Fault("{\"read_fail_pin\":12}");
        else Switch().Faulted = true;

        _now = _now.AddSeconds(400);                       // past the charter's min_off_s
        var blind = _host.ExecuteMission(Watering("m-2"), _now);
        AdvanceBoard(12);
        _host.ServiceActuations(_now = _now.AddSeconds(12));
        Beat();

        if (board is not null) board.Fault("{\"read_fail_pin\":-1}");
        else Switch().Faulted = false;

        var record = Records().Where(r => r.Capability == Valve).LastOrDefault();
        if (record is null) return Fail("no second actuation was recorded");
        if (record.Outcome != ActionOutcomes.Unverified)
            return Fail($"with the witness blind the record still reads '{record.Outcome}'");
        if (blind.State != MissionStates.Unverified && blind.State != MissionStates.Failed)
            return Fail($"the mission reported '{blind.State}' with nothing confirming it");
        return Ok($"the switch could not be read → the action is '{record.Outcome}', the mission '{blind.State}': {Trim(record.Detail)}");
    });

    // ------------------------------------------------------------------ 13

    private int _queuedWhileOffline;

    private void NetworkDropsAndWorkContinuesInsideTheLease() => Check(13, "the network drops: work continues inside the lease, inventing no authority, and evidence queues", () =>
    {
        var before = Records().Count;
        var leaseBefore = _host.Authority.LeaseExpiresAt;
        _link.Online = false;

        _now = _now.AddSeconds(400);
        var offline = _host.ExecuteMission(Watering("m-3"), _now);
        AdvanceBoard(12);
        _host.ServiceActuations(_now = _now.AddSeconds(12));
        var outcome = _host.Sync(_now);

        if (offline.State is not (MissionStates.Completed or MissionStates.Unverified))
            return Fail($"work inside the lease reported '{offline.State}': {offline.Detail}");
        if (outcome.Delivered) return Fail("the sync claimed delivery with the link down");
        if (_host.Authority.LeaseExpiresAt != leaseBefore) return Fail("the lease moved while the controller was unreachable");
        _queuedWhileOffline = Records().Count == before ? 1 : 0;
        if (_queuedWhileOffline == 0) return Fail("records reached the controller with the link down");
        return Ok($"mission '{offline.State}' offline; nothing delivered; the lease still expires at {leaseBefore:HH:mm:ss}Z, not later");
    });

    // ------------------------------------------------------------------ 14

    private void RebootRestoresEverything() => Check(14, "the Pi reboots and stop, lease, configuration and evidence restore", () =>
    {
        var leaseBefore = _host.Authority.LeaseExpiresAt;
        _host.PersistAuthority();

        _host = MoundHost.Create(new HostOptions
        {
            Keys = _keys,
            Manifest = _manifest,
            StateDirectory = StateDirectory,
            Drivers = Factories(),
            Transport = _link,
            ControllerKeys = _controllerKeys,
            Settle = Settle
        });
        var restored = _host.Restore(_now);

        if (_host.State != MoundStates.Chartered) return Fail($"the reborn mound is '{_host.State}'");
        if (_host.Authority.LeaseExpiresAt != leaseBefore) return Fail("the lease was re-minted across the restart");
        if (_host.Authority.DeviceLimitsFor(Valve)?.MaxOnSeconds is not 20) return Fail("the pushed configuration did not survive");
        var notes = restored.IsValid ? "" : " (" + string.Join("; ", restored.Errors) + ")";
        return Ok($"chartered again from disk, same lease expiry, device tier 20s intact{notes}");
    });

    // ------------------------------------------------------------------ 15

    private void LeaseExpiresIntoTheSafeState() => Check(15, "the lease expires while disconnected and the mound enters its declared safe state", () =>
    {
        // The lease as it stands NOW — a beat inside the lease may legitimately have renewed it, and a
        // criterion that tested a stale expiry would be testing its own bookkeeping, not the mound's.
        _leaseExpiry = _host.Authority.LeaseExpiresAt;

        // Nobody asks this mound to do anything. It is a service tick and nothing else: no mission, no
        // downlink, no operator. Quiescing has to be something the mound does by itself, or the lease
        // is not a safety property at all — it is only a check on the next request that happens to come.
        _now = _leaseExpiry.AddSeconds(1);
        var service = new MoundService(_host);
        service.Tick(_now);
        AdvanceBoard(1);

        if (_host.State != MoundStates.Quiesced) return Fail($"past the lease the mound is '{_host.State}'");
        if (board is not null && board.World().Pin(5).Level) return Fail("the output is still energized after quiescing");
        var refused = _host.ExecuteMission(Watering("m-4"), _now);
        if (refused.State == MissionStates.Completed) return Fail("a mission ran on an expired lease");
        return Ok($"an idle tick past the lease quiesced into '{_manifest.SafeState}' with nobody asking; " +
                  $"outputs de-energized, further work '{refused.State}'");
    });

    // ------------------------------------------------------------------ 16

    private void ReconnectDoesNotResumeAuthorityAndTheHistorySyncs() => Check(16, "the network returns: expired authority does not resume and the backlog syncs into a complete auditable history", () =>
    {
        _link.Online = true;
        for (var i = 0; i < 12; i++) _host.Sync(_now = _now.AddSeconds(1));

        if (_host.State != MoundStates.Quiesced) return Fail($"reconnecting resumed authority: the mound is '{_host.State}'");
        var account = _controller.Account(MoundId);
        if (account.Refusals > 0) return Fail($"the controller refused {account.Refusals} uplink envelope(s): {string.Join("; ", _controller.Audit.Take(3))}");
        if (account.Records.Count < 3) return Fail($"only {account.Records.Count} action record(s) arrived");
        if (account.Reports.Count < 3) return Fail($"only {account.Reports.Count} mission report(s) arrived");
        var unresolved = account.Records.SelectMany(r => r.EvidenceRefs).Where(id => !account.Evidence.ContainsKey(id)).ToList();
        if (unresolved.Count > 0) return Fail($"{unresolved.Count} evidence reference(s) the controller cannot resolve");
        return Ok($"{account.Records.Count} records, {account.Reports.Count} reports, {account.Evidence.Count} evidence items, " +
                  $"0 refusals, chain acknowledged through seq {account.AckedSeq}; the mound is still quiesced");
    });

    // ------------------------------------------------------------------ 17

    private void StopSurvivesARestart() => Check(17, "a stop de-energizes and is not cleared by a restart", () =>
    {
        _controller.IssueCharter(Charter(), _now);           // fresh authority, so the stop has something to stop
        _host.Sync(_now = _now.AddSeconds(1));
        if (_host.State != MoundStates.Chartered) return Fail("the mound could not be re-chartered before the stop");

        _controller.OrderStop(MoundId, "acceptance: operator stop", _now);
        _host.Sync(_now = _now.AddSeconds(1));
        AdvanceBoard(1);
        if (_host.State != MoundStates.Stopped) return Fail($"after the stop the mound is '{_host.State}'");
        if (board is not null && board.World().Pin(5).Level) return Fail("the output is still energized after the stop");

        _host.PersistAuthority();
        _host = MoundHost.Create(new HostOptions
        {
            Keys = _keys, Manifest = _manifest, StateDirectory = StateDirectory,
            Drivers = Factories(), Transport = _link, ControllerKeys = _controllerKeys, Settle = Settle
        });
        _host.Restore(_now);
        if (_host.State != MoundStates.Stopped) return Fail($"the restart cleared the stop: '{_host.State}'");
        var refused = _host.ExecuteMission(Watering("m-5"), _now);
        return Ok($"stopped, outputs de-energized, still stopped after a restart, further work '{refused.State}'");
    });

    // ------------------------------------------------------------------ 18

    private void TheBoardEnforcesItsOwnTier() => Check(18, "the board keeps its own limit tier and drops its outputs when the Pi goes quiet", () =>
    {
        if (board is null) return NotApplicable("this leg runs on in-memory ports; there is no firmware tier below the kernel");

        // Every actuation so far released on time, so the board's own bound never had to bite — which
        // is the correct outcome for a healthy Pi and no evidence at all about the tier below it. So
        // the harness now plays a Pi that dies mid-actuation: it drives the line over the link and
        // then says nothing more. Nothing above the board can help it from here.
        var drivenAt = board.World().Now;
        board.Drive(5, true);
        if (!board.World().Pin(5).Level) return Fail("the line did not go active when driven directly");

        // Past the board's compiled max_on_s (30 s) with no release coming.
        var released = board.Advance(35);
        var pin = released.Pin(5);
        if (pin.AutoReleases == 0) return Fail($"the board held the line {released.Now - drivenAt}s past its own bound without releasing it");
        if (pin.Level) return Fail("the board's own bound expired and the line is still energized");

        // And past its link watchdog with the Pi still silent (sim/* control paths are answered by the
        // simulator, never by the board, so nothing here counts as the link talking).
        var quiet = board.Advance(90);
        if (quiet.WatchdogTrips == 0) return Fail($"the board's link watchdog never fired after {quiet.Now - drivenAt}s of silence");
        if (quiet.Pins.Any(p => p.Level)) return Fail("a line was still energized after the watchdog fired");
        return Ok($"a Pi that died mid-actuation: the board released the line on its own {pin.AutoReleases} time(s) " +
                  $"at its compiled bound, then tripped its link watchdog {quiet.WatchdogTrips} time(s); every line safe");
    });

    // ---- the bench --------------------------------------------------------------------------

    private string StateDirectory => Path.Combine(stateRoot, "state");

    /// <summary>
    /// The drivers this leg composes from. On the firmware leg every line and channel is the board's,
    /// reached over the link; in memory the same three generic driver kinds are given a small world
    /// the harness can see into — one valve line, one limit switch wired to it, one tank channel.
    /// The manifest, the capabilities and the mission are identical either way.
    /// </summary>
    private DriverFactoryRegistry Factories()
    {
        if (board is not null) return MoundHost.HardwareDriverFactories(GpioBackings.Chardev, board);

        var factories = MoundHost.DefaultDriverFactories();
        factories.Register(new DigitalActuatorFactory(() => ValveLine));
        factories.Register(new DigitalSensorFactory(() => Switch()));
        factories.Register(new AnalogSensorFactory(() => Tank));
        return factories;
    }

    /// <summary>
    /// How a mission step's settle window passes here. There is no wall clock in this run: the wait is
    /// the board's simulated seconds going by, or — with no board — nothing to wait for, since the
    /// in-memory switch follows the valve line in the same instant. Either way the mission's clock
    /// moves by exactly what it asked for, and so does the bench's.
    /// </summary>
    private DateTimeOffset Settle(TimeSpan span, DateTimeOffset at)
    {
        AdvanceBoard((int)Math.Ceiling(span.TotalSeconds));
        var resumed = at + span;
        if (resumed > _now) _now = resumed;
        return resumed;
    }

    /// <summary>Long enough for the bench's switch to travel (2 s on the board), inside the protocol bound.</summary>
    private const double SettleWindow = 3;

    private void AdvanceBoard(int seconds)
    {
        if (board is not null && seconds > 0) board.Advance(seconds);
    }

    /// <summary>The valve line, in memory: the thing the switch below is bolted to.</summary>
    private InMemoryDigitalOutput ValveLine => _valve ??= new InMemoryDigitalOutput();
    private InMemoryDigitalOutput? _valve;

    /// <summary>The tank channel, in memory.</summary>
    private InMemoryAnalogInput Tank => _tank ??= new InMemoryAnalogInput { Value = 0.20 };
    private InMemoryAnalogInput? _tank;

    /// <summary>
    /// The in-memory limit switch, and the only interesting part of the fake world: it is an
    /// INDEPENDENT observation only because it reads the valve LINE rather than the command that drove
    /// it. A switch the harness simply set to "closed" after each act step would confirm nothing, and
    /// a criterion it satisfied would be worth nothing.
    /// </summary>
    private BenchSwitch Switch() => _switch ??= new BenchSwitch(ValveLine);
    private BenchSwitch? _switch;

    /// <summary>An active-low limit switch: closed (physically low) exactly while the line is driven.</summary>
    private sealed class BenchSwitch(InMemoryDigitalOutput line) : IDigitalInput
    {
        /// <summary>Set to make the line unreadable, as a cut wire is.</summary>
        public bool Faulted { get; set; }

        public bool Read() => Faulted ? throw new IOException("the line could not be read") : !line.State;
    }

    /// <summary>
    /// A sync beat, as the service loop would run one. Records leave the mound on a beat, not at the
    /// moment they are made, so every criterion that reads the controller's copy has to let one happen
    /// first — and doing it here, rather than reaching into the queue, is the same path the daemon uses.
    /// </summary>
    private void Beat() => _host.Sync(_now = _now.AddSeconds(1));

    private IReadOnlyList<ActionRecord> Records() => _controller.Account(MoundId).Records;
    private IReadOnlyDictionary<string, EvidenceItem> Evidence() => _controller.Account(MoundId).Evidence;

    private MoundManifest Manifest()
    {
        var linked = board is not null;
        var manifest = new MoundManifest
        {
            ManifestId = "mf-acceptance-1",
            MoundId = MoundId,
            IssuedAt = _now.ToWire(),
            Capabilities = [Valve, Level, Closed],
            SafeState = "all_actuators_off",
            DeviceLimits = { [Valve] = new CapabilityLimits { MaxOnSeconds = 25 } }
        };
        manifest.Hardware["irrigation"] = new HardwareBinding
        {
            Driver = "digital_actuator",
            Settings = Settings(("capability", Valve), ("active_high", "true"), ("max_on_s", "60"), ("min_off_s", "300"), ("max_rate_per_h", "6"))
        };
        manifest.Hardware["tank"] = new HardwareBinding
        {
            Driver = "analog_sensor",
            Settings = Settings(("capability", Level), ("unit", "pct"), ("scale", "100"), ("offset", "0"))
        };
        manifest.Hardware["limit"] = new HardwareBinding
        {
            Driver = "digital_sensor",
            Settings = Settings(("capability", Closed), ("active_high", "false"), ("unit", "closed"))
        };
        if (linked)
        {
            manifest.Hardware["irrigation"].Settings["link"] = BoardProcess.LinkName;
            manifest.Hardware["irrigation"].Settings["pin"] = "5";
            manifest.Hardware["tank"].Settings["link"] = BoardProcess.LinkName;
            manifest.Hardware["tank"].Settings["channel"] = "0";
            manifest.Hardware["limit"].Settings["link"] = BoardProcess.LinkName;
            manifest.Hardware["limit"].Settings["pin"] = "12";
        }
        return manifest;
    }

    private static Dictionary<string, string> Settings(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private Charter Charter() => new()
    {
        CharterId = "c-acceptance",
        MoundId = MoundId,
        MissionRef = "acceptance",
        IssuedAt = _now.ToWire(),
        ExpiresAt = _now.AddHours(4).ToWire(),
        LeaseTtlSeconds = 3600,
        ActionCeiling = "benign",
        Capabilities = [Valve, Level, Closed],
        Limits = { [Valve] = new CapabilityLimits { MaxOnSeconds = 10, MinOffSeconds = 300 } },
        Evidence = new EvidencePolicy { RequiredFor = ["act.*"], MinIntervalSeconds = 3600 },
        SafeState = "all_actuators_off"
    };

    /// <summary>Read the tank, open the valve for longer than anything allows, then ask the switch whether it moved.</summary>
    private Mission Watering(string id) => new()
    {
        MissionId = id,
        MoundId = MoundId,
        CharterId = "c-acceptance",
        RequiredCapabilities = [Valve, Level, Closed],
        Steps =
        [
            new MissionStep { StepId = "before", Op = MissionStepOps.Sense, Capability = Level, EvidenceTag = "level_before" },
            new MissionStep { StepId = "fill", Op = MissionStepOps.Act, Capability = Valve, Parameters = { ["on_s"] = 60 }, EvidenceTag = "filling" },
            new MissionStep
            {
                StepId = "confirm", Op = MissionStepOps.Verify, Capability = Closed,
                EvidenceTag = "moved", Confirms = "fill", SettleSeconds = SettleWindow,
                // The postcondition (`v0.9.30`). Before it, this step asked only whether something
                // independent had looked; criterion 11 passed on a switch reading either value.
                Expect = new StepExpectation { Op = ConditionOps.Equal, Value = 1, Unit = "closed" }
            }
        ],
        RequiredFeatures = [ProtocolFeatures.Postconditions],
        // NOT "filling": the act step's tag names evidence an actuator cannot produce. A digital
        // actuator returns a command result, and a command is not evidence of physical work — that is
        // exactly what the confirming read of the limit switch is for. Requiring a tag no honest
        // driver can emit would grade every mission `unverified` for a fact about the driver kind.
        RequiredEvidence = ["level_before", "moved"],
        SafeState = "all_actuators_off",
        ExpiresAt = _now.AddHours(1).ToWire(),
        Context = "acceptance: fill the tank and confirm the valve moved"
    };

    private static IEnumerable<Assembly> CoreAssemblies() =>
        new[] { typeof(Envelope), typeof(CapabilityKernel), typeof(MoundMajor), typeof(DigitalActuatorDriver), typeof(MoundHost) }
            .Select(t => t.Assembly)
            .Distinct();

    private sealed class SimEnrollmentClient(SimController controller) : IEnrollmentClient
    {
        public bool TryEnroll(string token, byte[] devicePublicKey, out ControllerEnrollment? enrollment, out string detail)
        {
            enrollment = new ControllerEnrollment(controller.Enroll(MoundId, devicePublicKey), MoundId, SyncIntervalSeconds: 15);
            detail = "enrolled (controller asks for a 15s sync cadence)";
            return true;
        }
    }

    // ---- reporting --------------------------------------------------------------------------

    private void Check(int number, string name, Func<Outcome> body)
    {
        Outcome outcome;
        try
        {
            outcome = body();
        }
        catch (Exception ex)
        {
            outcome = new Outcome(Verdict.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
        _criteria.Add(new Criterion(number, name, outcome.Verdict, outcome.Detail));
        trace?.Invoke($"{number,3}  {outcome.Verdict}  {name} — {outcome.Detail}");
    }

    private static Outcome Ok(string detail) => new(Verdict.Met, detail);
    private static Outcome Fail(string detail) => new(Verdict.Failed, detail);
    private static Outcome NotApplicable(string detail) => new(Verdict.NotApplicable, detail);

    private static string Short(string ant) => ant.Replace(" Ant", "", StringComparison.Ordinal);
    private static string Join<T>(IEnumerable<T> values) => string.Join("/", values);
    private static string Trim(string detail) => detail.Length <= 90 ? detail : detail[..90] + "…";

    private readonly record struct Outcome(Verdict Verdict, string Detail);
}

public enum Verdict { Met, Failed, NotApplicable }

public sealed record Criterion(int Number, string Name, Verdict Verdict, string Detail);
