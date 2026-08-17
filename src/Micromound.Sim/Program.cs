using Micromound.Crypto;
using Micromound.Protocol;
using Micromound.Sim;

// Demo run for a greenhouse mound: configure hardware, charter it, watch the three limit tiers
// intersect, watch a dead sensor turn a completed actuation into `unverified`, go offline, expire
// the lease, then reconnect and drain the signed, chained backlog exactly as a controller would
// verify it.
//
// This is a smoke script, not the test suite — the authority rules are proven in
// tests/Micromound.Tests, against the same kernel this runs.

var now = DateTimeOffset.UtcNow;

var mound = new SimMound("mm-greenhouse-01")
{
    DeviceCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "sense.soil_moisture", "sense.temperature", "act.water_valve"
    },
    FirmwareLimits = new Dictionary<string, CapabilityLimits>(StringComparer.Ordinal)
    {
        // What the valve physically tolerates. Nothing above this can widen it.
        ["act.water_valve"] = new CapabilityLimits { MaxOnSeconds = 30, MinOffSeconds = 300, MaxRatePerHour = 6 }
    }
};

Console.WriteLine($"[{mound.MoundId}] tier={mound.Tier} state={mound.State}");

// Enrollment (PROTOCOL.md §3), compressed: the controller binds the device's public key.
var directory = new InMemoryPublicKeyDirectory();
directory.Register(mound.MoundId, mound.PublicKey);
var verifier = new Ed25519EnvelopeVerifier(directory);

// Sensing needs no charter — "no charter means observe only" is a grant, not only a prohibition.
Console.WriteLine($"sense with no charter   -> {mound.Sense("sense.soil_moisture", now).Outcome}");
Console.WriteLine($"actuate with no charter -> {mound.Actuate("act.water_valve", now, 10).Detail}");

// Tier 2: operator configuration, narrower than the hardware and independent of any mission.
var manifest = new MoundManifest
{
    ManifestId = Guid.NewGuid().ToString(),
    MoundId = mound.MoundId,
    IssuedAt = now.ToWire(),
    Capabilities = ["sense.soil_moisture", "sense.temperature", "act.water_valve"],
    DeviceLimits = { ["act.water_valve"] = new CapabilityLimits { MaxOnSeconds = 20 } },
    Reasoning = new ReasoningConfig { Mode = ReasoningModes.None },
    SafeState = "all_actuators_off"
};

Console.WriteLine($"manifest applied={mound.ApplyManifest(manifest).IsValid} (device max_on_s=20)");

// Tier 3: the delegated grant. It narrows on-time to 10 s, and *tries* to relax the duty cycle
// to 5 s — watch the effective limits keep the firmware's 300 s instead.
var charter = new Charter
{
    CharterId = Guid.NewGuid().ToString(),
    MoundId = mound.MoundId,
    MissionRef = "demo-mission",
    IssuedAt = now.ToWire(),
    ExpiresAt = now.AddHours(1).ToWire(),
    LeaseTtlSeconds = 900,
    ActionCeiling = "benign",
    Capabilities = ["sense.soil_moisture", "sense.temperature", "act.water_valve"],
    Limits = { ["act.water_valve"] = new CapabilityLimits { MaxOnSeconds = 10, MinOffSeconds = 5 } },
    Evidence = new EvidencePolicy { RequiredFor = ["act.*", "routine.*"], MinIntervalSeconds = 60 },
    SafeState = "all_actuators_off"
};

var offer = mound.OfferCharter(charter, now);
Console.WriteLine($"charter accepted={offer.IsValid} state={mound.State}");

foreach (var note in mound.Kernel.ReviewCharter(charter).Errors)
    Console.WriteLine($"  charter review: {note}");

var effective = mound.EffectiveLimits("act.water_valve");
Console.WriteLine($"effective act.water_valve: max_on_s={effective.MaxOnSeconds} " +
                  $"min_off_s={effective.MinOffSeconds} max_rate_per_h={effective.MaxRatePerHour}");

// Ask for 60 s. Hardware says 30, the manifest says 20, the charter says 10 — narrowest wins.
var clamped = mound.Actuate("act.water_valve", now, requestedOnSeconds: 60);
Console.WriteLine($"actuate 60s -> {clamped.Outcome} ({clamped.Detail}), evidence={clamped.EvidenceRefs.Count}");

// Too soon: the firmware's 300 s duty cycle refuses this, whatever the charter asked for.
var tooSoon = mound.Actuate("act.water_valve", now.AddSeconds(30), requestedOnSeconds: 5);
Console.WriteLine($"actuate 30s later -> {tooSoon.Outcome} ({tooSoon.Detail})");

// The same action with a dead sensor: the valve may well have opened, but nothing observed it.
mound.SensorHealthy = false;
var blind = mound.Actuate("act.water_valve", now.AddSeconds(400), requestedOnSeconds: 5);
Console.WriteLine($"actuate with dead sensor -> {blind.Outcome} ({blind.Detail})");
mound.SensorHealthy = true;

// Offline: the lease runs down; no renewal is possible from the device side.
var later = now.AddSeconds(charter.LeaseTtlSeconds + 1);
Console.WriteLine($"lease expired -> quiesced={mound.QuiesceIfExpired(later)} state={mound.State}");

var refused = mound.Actuate("act.water_valve", later, 5);
Console.WriteLine($"actuate after expiry -> {refused.Outcome} ({refused.Detail})");
Console.WriteLine($"sense after expiry   -> {mound.Sense("sense.temperature", later).Outcome}");

// Reconnect: drain the backlog, verify chain and signatures end to end like a controller would.
var backlog = mound.DrainUplink();
var chain = EnvelopeValidator.ValidateChain(backlog, "", verifier, mound.MoundId);
Console.WriteLine($"reconnect: {backlog.Count} envelopes, chain+signatures valid={chain.IsValid}");

// And what an impostor gets: the same bytes, under a key the controller never enrolled.
var impostorDirectory = new InMemoryPublicKeyDirectory();
impostorDirectory.Register(mound.MoundId, Ed25519KeyPair.Generate().PublicKey);
var impostor = EnvelopeValidator.ValidateChain(
    backlog, "", new Ed25519EnvelopeVerifier(impostorDirectory), mound.MoundId);
Console.WriteLine($"same backlog under a wrong key -> valid={impostor.IsValid} " +
                  $"({impostor.Errors.Count} refusals)");
