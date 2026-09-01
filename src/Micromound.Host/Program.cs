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
//
// A real network transport to the controller, and the timing watchdog's own thread, are the next M4
// slice; until then the daemon runs offline (the durable queue holds the backlog) and the watchdog
// is driven by the tick loop. All user-facing configuration and visualization belong to the upstream
// controller — see docs/UPSTREAM.md.

using System.Runtime.InteropServices;
using System.Text.Json;
using Micromound.Host;
using Micromound.Protocol;

var options = HostArgs.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(
        "usage: micromound --manifest <path> [--state <dir>] [--controller <url>] [--interval-s <n>] [--heartbeat-s <n>]\n" +
        "  --manifest    path to the mound manifest (JSON). required.\n" +
        "  --state       state root (identity + durable state). default: /var/lib/micromound\n" +
        "  --controller  controller base URL, e.g. https://anthill.example. default: offline\n" +
        "  --interval-s  seconds between service ticks. default: 5\n" +
        "  --heartbeat-s watchdog heartbeat timeout; 0 disables the timing check. default: 30");
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

MoundHost host;
MoundService service;
try
{
    var keys = MoundHost.LoadOrCreateIdentity(options.StateDirectory);
    host = MoundHost.Create(new HostOptions
    {
        Keys = keys,
        Manifest = manifest,
        StateDirectory = options.StateDirectory,
        GuardHeartbeatTimeoutSeconds = options.HeartbeatTimeoutSeconds,
        Transport = options.ControllerUrl is null ? null : new HttpSyncTransport(new Uri(options.ControllerUrl))
    });
    service = new MoundService(host);
    host.Restore(DateTimeOffset.UtcNow);   // recover any mission a prior run left in flight
}
catch (HostStartupException ex)
{
    // Fail closed: a mound that cannot come up safely does not come up at all.
    Console.Error.WriteLine($"micromound: bring-up refused (fail-closed): {ex.Message}");
    return 1;
}

Console.WriteLine(
    $"micromound: {host.MoundId} up, state={host.State}. Running offline until a controller " +
    "transport is configured; Ctrl-C / SIGTERM to stop safely.");

using var cts = new CancellationTokenSource();
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });

try
{
    while (!cts.IsCancellationRequested)
    {
        service.Tick(DateTimeOffset.UtcNow);
        try { await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), cts.Token); }
        catch (TaskCanceledException) { /* a shutdown signal; fall through to the safe stop */ }
    }
}
finally
{
    service.Shutdown(DateTimeOffset.UtcNow);
    Console.WriteLine("micromound: safe state entered, authority persisted. Stopped.");
}

return 0;

/// <summary>Parsed daemon arguments. Null from <see cref="Parse"/> means "print usage and exit".</summary>
file sealed record HostArgs(string ManifestPath, string StateDirectory, string? ControllerUrl, double IntervalSeconds, double HeartbeatTimeoutSeconds)
{
    public static HostArgs? Parse(string[] args)
    {
        string? manifest = null;
        var state = Environment.GetEnvironmentVariable("MICROMOUND_STATE") ?? "/var/lib/micromound";
        string? controller = null;
        var interval = 5.0;
        var heartbeat = 30.0;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest" when i + 1 < args.Length: manifest = args[++i]; break;
                case "--state" when i + 1 < args.Length: state = args[++i]; break;
                // PROTOCOL.md §1: device-initiated HTTPS only. A non-https (or malformed) URL is a
                // usage error, never a cleartext or undialable transport handed to the daemon.
                case "--controller" when i + 1 < args.Length && Uri.TryCreate(args[i + 1], UriKind.Absolute, out var cu) && cu.Scheme == Uri.UriSchemeHttps: controller = args[++i]; break;
                case "--interval-s" when i + 1 < args.Length && double.TryParse(args[i + 1], out var iv) && iv > 0: interval = iv; i++; break;
                case "--heartbeat-s" when i + 1 < args.Length && double.TryParse(args[i + 1], out var hb) && hb >= 0: heartbeat = hb; i++; break;
                default: return null;   // unknown or malformed argument → usage
            }
        }

        return manifest is null ? null : new HostArgs(manifest, state, controller, interval, heartbeat);
    }
}
