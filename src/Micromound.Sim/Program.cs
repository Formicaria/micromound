using Micromound.Crypto;
using Micromound.Protocol;
using Micromound.Sim;

// Demo run: charter a simulated mound, actuate with evidence, watch a limit clamp and a dead
// sensor produce `unverified`, go "offline", expire the lease, then reconnect and drain the
// signed, chained backlog exactly as the colony would verify it. This is a smoke script, not
// the test suite — authority rules are proven in tests/Micromound.Tests.

var now = DateTimeOffset.UtcNow;
var mound = new SimMound("mm-sim-001");

Console.WriteLine($"[{mound.MoundId}] state={mound.State}");

// Enrollment (PROTOCOL.md §3), compressed: the colony binds the device's public key.
var directory = new InMemoryPublicKeyDirectory();
directory.Register(mound.MoundId, mound.PublicKey);
var verifier = new Ed25519EnvelopeVerifier(directory);

var charter = new Charter
{
    CharterId = Guid.NewGuid().ToString(),
    MoundId = mound.MoundId,
    MissionRef = "demo-mission",
    IssuedAt = now.ToWire(),
    ExpiresAt = now.AddHours(1).ToWire(),
    LeaseTtlSeconds = 900,
    ActionCeiling = "benign",
    Capabilities = ["sense.temp", "act.relay_1"],
    // The charter narrows firmware's 30 s ceiling to 10 s. It also *tries* to relax the duty
    // cycle to 5 s — watch the effective limits below keep firmware's 300 s instead.
    Limits = { ["act.relay_1"] = new CapabilityLimits { MaxOnSeconds = 10, MinOffSeconds = 5 } },
    Evidence = new EvidencePolicy { RequiredFor = ["act.*"], MinIntervalSeconds = 60 },
    SafeState = "all_actuators_off"
};

var offer = mound.OfferCharter(charter, now);
Console.WriteLine($"charter accepted={offer.IsValid} state={mound.State}");

var effective = mound.EffectiveLimits("act.relay_1");
Console.WriteLine($"effective limits act.relay_1: max_on_s={effective.MaxOnSeconds} " +
                  $"min_off_s={effective.MinOffSeconds} max_rate_per_h={effective.MaxRatePerHour}");

// Ask for 60 s. Firmware says 30, the charter says 10 — the narrower wins, loudly.
var clampedRun = mound.Actuate("act.relay_1", now, requestedOnSeconds: 60);
Console.WriteLine($"actuate 60s -> {clampedRun.Outcome} ({clampedRun.Detail}), " +
                  $"evidence={clampedRun.EvidenceRefs.Count}");

// Too soon: firmware's 300 s duty cycle refuses this, whatever the charter asked for.
var tooSoon = mound.Actuate("act.relay_1", now.AddSeconds(30), requestedOnSeconds: 5);
Console.WriteLine($"actuate 30s later -> {tooSoon.Outcome} ({tooSoon.Detail})");

// Same action with a dead sensor: the relay may well have fired, but nothing observed it.
mound.SensorHealthy = false;
var blind = mound.Actuate("act.relay_1", now.AddSeconds(400), requestedOnSeconds: 5);
Console.WriteLine($"actuate with dead sensor -> {blind.Outcome} ({blind.Detail})");
mound.SensorHealthy = true;

// Offline: lease runs down; no renewal possible from the device side.
var later = now.AddSeconds(charter.LeaseTtlSeconds + 1);
var quiesced = mound.QuiesceIfExpired(later);
Console.WriteLine($"lease expired -> quiesced={quiesced} state={mound.State}");

var refused = mound.Actuate("act.relay_1", later);
Console.WriteLine($"actuate after expiry -> {refused.Outcome} ({refused.Detail})");

// Reconnect: drain backlog, verify chain and signatures end-to-end like the colony would.
var backlog = mound.DrainUplink();
var chain = EnvelopeValidator.ValidateChain(backlog, "", verifier, mound.MoundId);
Console.WriteLine($"reconnect: {backlog.Count} envelopes, chain+signatures valid={chain.IsValid}");

// And what an impostor gets: same bytes, a key the colony never enrolled.
var impostorDirectory = new InMemoryPublicKeyDirectory();
impostorDirectory.Register(mound.MoundId, Ed25519KeyPair.Generate().PublicKey);
var impostor = EnvelopeValidator.ValidateChain(
    backlog, "", new Ed25519EnvelopeVerifier(impostorDirectory), mound.MoundId);
Console.WriteLine($"same backlog under a wrong key -> valid={impostor.IsValid} " +
                  $"({impostor.Errors.Count} refusals)");
