// Micromound host — the headless Raspberry Pi / Linux daemon.
//
// Composition order (build order = the order in which authority precedes anything physical):
//   1. Identity      — load or generate the device Ed25519 keypair (MoundHost.LoadOrCreateIdentity)
//   2. Manifest      — load and validate the mound manifest, fail closed
//   3. Bring-up      — MoundHost.Create: resolve drivers, compose over the durable file store, apply
//   4. Recover       — MoundHost.Restore: recover any mission a prior run left in flight, fail-closed
//   5. Serve         — MoundService tick loop: heartbeat, sync beat, watchdog; safe shutdown on signal
//
// Local layout (operator-configured via --state, default /var/lib/micromound):
//   <state>/identity/seed   device keypair, owner-only, never transmitted
//   <state>/state/          durable operational state (charter, mission checkpoint, uplink queue)
//   <state>/evidence/       durable evidence store (one file per item, ack markers, spill counters)
//
// With --controller the daemon enrolls (once, by a one-time token), then syncs signed envelopes on the
// controller's cadence; without it, it runs offline and the durable queue holds the backlog. An
// independent watchdog thread de-energizes and stops the mound if this loop hangs. All user-facing
// configuration and visualization belong to the upstream controller — see docs/UPSTREAM.md.

using System.Runtime.InteropServices;
using System.Text.Json;
using Micromound.Drivers;
using Micromound.Host;
using Micromound.Protocol;

// `--describe-drivers`: print the driver-type catalog this build ships — what a manifest may bind and
// the settings each type reads — as JSON, for a controller or a person building a hardware form. The
// same data goes to the controller at enrollment (`driver_schemas`). Then exit; no mound comes up.
if (args.Length > 0 && args.Contains("--describe-drivers", StringComparer.Ordinal))
{
    var registry = args.Contains("--hardware", StringComparer.Ordinal) ? MoundHost.HardwareDriverFactories(HostArgs.GpioBackingOf(args)) : MoundHost.DefaultDriverFactories();
    Console.WriteLine(JsonSerializer.Serialize(registry.Describe(), new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }));
    return 0;
}

var options = HostArgs.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(
        "usage: micromound --manifest <path> [--hardware [--gpio chardev|sysfs] | --simulate] [--state <dir>] [--controller <url>] [--enroll-token <t>] [--tier <t>] [--interval-s <n>] [--heartbeat-s <n>] [--watchdog-s <n>]\n" +
        "       micromound --manifest <path> --check-hardware [--gpio chardev|sysfs]   claim every port the manifest names, read each sensor once, report, exit (0 = all claimed)\n" +
        "       micromound --describe-drivers [--hardware]   print the driver types this build ships and their settings (JSON), then exit\n" +
        "  --manifest     path to the mound manifest (JSON). required.\n" +
        "  --hardware     drive REAL ports: digital actuators on a GPIO line (settings 'pin', 'chip'), analog\n" +
        "                 sensors on an ADS1115 over I2C (settings 'channel', 'bus', 'address', 'gain'). Without it\n" +
        "                 every port is in-memory — nothing physical moves and readings are zero.\n" +
        "  --gpio         GPIO backing with --hardware: chardev (/dev/gpiochipN, the libgpiod interface; default)\n" +
        "                 or sysfs (legacy /sys/class/gpio, for kernels that still ship it).\n" +
        "  --simulate     run a manifest that names physical ports (pin, channel, bus, address) on IN-MEMORY ports\n" +
        "                 anyway — for a development machine. Without --hardware or --simulate such a manifest\n" +
        "                 is refused, because its readings and actuations would look real and be neither.\n" +
        "  --check-hardware  open every device exactly as bring-up would (real ports, safe level), take one\n" +
        "                 reading per sensor, print a per-device report, exit. Actuates nothing; composes no mound.\n" +
        "  --state        state root (identity + durable state). default: /var/lib/micromound\n" +
        "  --controller   controller base URL, e.g. https://anthill.example. default: offline\n" +
        "  --enroll-token one-time enrollment token; used once if not already enrolled (PROTOCOL.md §3)\n" +
        "  --tier         controller tier declared at enrollment: edge_queen (a Pi running the full\n" +
        "                 colony) or deterministic_controller (a constrained subordinate). default: edge_queen\n" +
        "  --interval-s   seconds between service ticks (hold release, watchdog, heartbeat). default: 5.\n" +
        "                 The SYNC cadence follows the controller's sync_interval_s once enrolled.\n" +
        "  --heartbeat-s  soft watchdog heartbeat timeout; 0 disables the timing check. default: 30\n" +
        "  --watchdog-s   HARD independent-watchdog timeout: an own-thread timer that de-energizes and\n" +
        "                 stops the mound if the service loop hangs this long. 0 disables; omitted\n" +
        "                 auto-derives max(3*heartbeat, 6*interval). Keep it generous to avoid false trips.");
    return 2;
}

MoundManifest manifest;
try
{
    var json = File.ReadAllText(options.ManifestPath);
    manifest = JsonSerializer.Deserialize<MoundManifest>(json, ProtocolJson.Options)
        ?? throw new InvalidOperationException("manifest deserialized to nothing");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"micromound: cannot read manifest '{options.ManifestPath}': {ex.Message}");
    return 2;
}

// `--check-hardware`: the operator's question at the board — does the wiring match the manifest?
// Every port is opened exactly as bring-up would open it; each sensor is read once; nothing is
// actuated and no mound is composed. Exit 0 only if every device was claimed.
if (options.CheckHardware)
{
    var report = HardwareCheck.Run(manifest, MoundHost.HardwareDriverFactories(options.GpioBacking), DateTimeOffset.UtcNow);
    Console.WriteLine(HardwareCheck.Format(report, "real ports, GPIO via " + options.GpioBacking));
    return report.AllOk ? 0 : 1;
}

// A manifest that names PHYSICAL ports (a pin, an I2C channel) while the daemon runs in-memory would
// produce readings and "actuations" that look real and are not. That is REFUSED unless the operator
// says --simulate in so many words; a development machine can, a device must not by accident.
if (!options.Hardware)
{
    var physical = HardwareCheck.DevicesNamingPhysicalPorts(manifest);
    if (physical.Count > 0 && !options.Simulate)
    {
        Console.Error.WriteLine($"micromound: refused (fail-closed): the manifest names physical ports for {string.Join(", ", physical)} but neither --hardware nor --simulate was given. " +
                                "On a device pass --hardware; on a development machine pass --simulate to run every port in memory (nothing physical is driven or measured).");
        return 2;
    }
    if (physical.Count > 0)
        Console.Error.WriteLine($"micromound: SIMULATING: manifest names physical ports for {string.Join(", ", physical)}; every port is IN-MEMORY and nothing physical is driven or measured.");
}

MoundHost host;
MoundService service;
try
{
    var keys = MoundHost.LoadOrCreateIdentity(options.StateDirectory);

    // Real ports only when asked for: a manifest naming a pin or an I2C address opens it fail-closed.
    // Chosen before enrollment because the device describes THESE factories to the controller.
    var factories = options.Hardware ? MoundHost.HardwareDriverFactories(options.GpioBacking) : MoundHost.DefaultDriverFactories();

    // Enrollment (PROTOCOL.md §3): with a controller configured, load the controller key from a prior
    // enrollment or present the one-time token now. Without it, downlink stays unverifiable — the safe
    // direction — and the mound only uplinks.
    IPublicKeyDirectory controllerKeys = new InMemoryPublicKeyDirectory();
    double? controllerSyncInterval = null;
    if (options.ControllerUrl is not null)
    {
        // The device tells the controller what it is: its manifest mound id (a cross-check against the
        // mound the operator minted the token for), its tier (one the controller accepts), its
        // capabilities, and its protocol version — so a misconfiguration is refused at the door,
        // loudly, instead of surfacing as an unexplained signature refusal on every later beat.
        using var enroller = new HttpEnrollmentClient(new Uri(options.ControllerUrl),
            hardwareProfile: string.Join(",", manifest.Capabilities),
            tier: options.Tier,
            moundId: manifest.MoundId,
            capabilities: manifest.Capabilities,
            driverSchemas: factories.Describe());
        var link = MoundHost.ResolveControllerLink(options.StateDirectory, enroller, keys.PublicKey, options.EnrollToken);
        controllerKeys = link.Keys;
        controllerSyncInterval = link.SyncIntervalSeconds;
        Console.WriteLine($"micromound: {link.Detail}");
    }

    host = MoundHost.Create(new HostOptions
    {
        Keys = keys,
        Manifest = manifest,
        StateDirectory = options.StateDirectory,
        Drivers = factories,
        GuardHeartbeatTimeoutSeconds = options.HeartbeatTimeoutSeconds,
        ControllerKeys = controllerKeys,
        Transport = options.ControllerUrl is null ? null : new HttpSyncTransport(new Uri(options.ControllerUrl))
    });
    service = new MoundService(host);
    // Honour the controller's sync cadence if it stated one. This throttles the sync beat ONLY; the
    // tick — hold release, watchdog kick, heartbeat — keeps --interval-s regardless.
    if (controllerSyncInterval is { } cadence)
        service.SyncInterval = TimeSpan.FromSeconds(cadence);
    host.Restore(DateTimeOffset.UtcNow);   // recover any mission a prior run left in flight
}
catch (HostStartupException ex)
{
    // Fail closed: a mound that cannot come up safely does not come up at all.
    Console.Error.WriteLine($"micromound: bring-up refused (fail-closed): {ex.Message}");
    return 1;
}

// The HARD watchdog: an independent thread that de-energizes and stops the mound if this loop hangs
// long enough that a held actuation could stay hot behind it (the soft, loop-driven heartbeat cannot
// release a line the loop is no longer running to release). Omitted → derive a generous default from
// the heartbeat and tick interval; 0 → disabled.
var watchdogSeconds = options.WatchdogSeconds
    ?? Math.Max(options.HeartbeatTimeoutSeconds > 0 ? options.HeartbeatTimeoutSeconds * 3 : 0, options.IntervalSeconds * 6);

Console.WriteLine(
    $"micromound: {host.MoundId} up, state={host.State}. " +
    (options.Hardware ? $"Ports: REAL hardware (GPIO via {options.GpioBacking}, ADS1115/I2C). " : "Ports: IN-MEMORY (no --hardware; nothing physical is driven). ") +
    (options.ControllerUrl is null ? "Running offline (no --controller); " : $"Controller {options.ControllerUrl}; ") +
    "Ctrl-C / SIGTERM to stop safely." +
    (watchdogSeconds > 0 ? $" Independent watchdog armed at {watchdogSeconds:0.#}s." : " Independent watchdog disabled."));

using var cts = new CancellationTokenSource();
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });

WatchdogThread? watchdog = null;
if (watchdogSeconds > 0)
{
    watchdog = new WatchdogThread(TimeSpan.FromSeconds(watchdogSeconds), () =>
    {
        Console.Error.WriteLine("micromound: WATCHDOG fired — service loop unresponsive; de-energizing and stopping.");
        host.WatchdogStop($"service loop unresponsive for {watchdogSeconds:0.#}s");
    });
    watchdog.Start();
}

try
{
    while (!cts.IsCancellationRequested)
    {
        service.Tick(DateTimeOffset.UtcNow);
        watchdog?.Kick();   // a full tick completed: the loop is alive
        try { await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), cts.Token); }
        catch (TaskCanceledException) { /* a shutdown signal; fall through to the safe stop */ }
    }
}
finally
{
    watchdog?.Dispose();   // stop watching before the deliberate shutdown, so a clean stop is not a "hang"
    service.Shutdown(DateTimeOffset.UtcNow);
    Console.WriteLine("micromound: safe state entered, authority persisted. Stopped.");
}

return 0;

/// <summary>Parsed daemon arguments. Null from <see cref="Parse"/> means "print usage and exit".</summary>
file sealed record HostArgs(string ManifestPath, bool Hardware, bool Simulate, bool CheckHardware, string GpioBacking, string StateDirectory, string? ControllerUrl, string? EnrollToken, string Tier, double IntervalSeconds, double HeartbeatTimeoutSeconds, double? WatchdogSeconds)
{
    /// <summary>The <c>--gpio</c> value in a raw argument list, or the default; used before full parsing.</summary>
    public static string GpioBackingOf(string[] args)
    {
        var i = Array.IndexOf(args, "--gpio");
        return i >= 0 && i + 1 < args.Length && GpioBackings.IsKnown(args[i + 1]) ? args[i + 1] : GpioBackings.Chardev;
    }

    public static HostArgs? Parse(string[] args)
    {
        string? manifest = null;
        var hardware = false;   // in-memory ports unless the operator asks for the real ones
        var simulate = false;   // ...and a manifest naming physical ports needs this to run in memory
        var check = false;      // --check-hardware: report and exit
        var gpio = GpioBackings.Chardev;
        var state = Environment.GetEnvironmentVariable("MICROMOUND_STATE") ?? "/var/lib/micromound";
        string? controller = null;
        string? enrollToken = null;
        var tier = ControllerTiers.EdgeQueen;   // a Pi running the full colony, unless told otherwise
        var interval = 5.0;
        var heartbeat = 30.0;
        double? watchdog = null;   // null → auto-derive a safe default; 0 → explicitly disabled

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest" when i + 1 < args.Length: manifest = args[++i]; break;
                case "--hardware": hardware = true; break;
                case "--simulate": simulate = true; break;
                case "--check-hardware": check = true; break;
                case "--gpio" when i + 1 < args.Length && GpioBackings.IsKnown(args[i + 1]): gpio = args[++i]; break;
                case "--state" when i + 1 < args.Length: state = args[++i]; break;
                // PROTOCOL.md §1: device-initiated HTTPS only. A non-https (or malformed) URL is a
                // usage error, never a cleartext or undialable transport handed to the daemon.
                case "--controller" when i + 1 < args.Length && Uri.TryCreate(args[i + 1], UriKind.Absolute, out var cu) && cu.Scheme == Uri.UriSchemeHttps: controller = args[++i]; break;
                case "--enroll-token" when i + 1 < args.Length: enrollToken = args[++i]; break;
                // Only a tier the controller accepts; an unknown one would be refused at enrollment anyway,
                // so refuse it here as a usage error with the vocabulary in the message.
                case "--tier" when i + 1 < args.Length && ControllerTiers.IsKnown(args[i + 1]): tier = args[++i]; break;
                case "--interval-s" when i + 1 < args.Length && double.TryParse(args[i + 1], out var iv) && iv > 0: interval = iv; i++; break;
                case "--heartbeat-s" when i + 1 < args.Length && double.TryParse(args[i + 1], out var hb) && hb >= 0: heartbeat = hb; i++; break;
                case "--watchdog-s" when i + 1 < args.Length && double.TryParse(args[i + 1], out var wd) && wd >= 0: watchdog = wd; i++; break;
                default: return null;   // unknown or malformed argument → usage
            }
        }

        if (hardware && simulate) return null;   // contradictory: usage
        return manifest is null ? null : new HostArgs(manifest, hardware, simulate, check, gpio, state, controller, enrollToken, tier, interval, heartbeat, watchdog);
    }
}
