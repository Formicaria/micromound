using Micromound.Protocol;
using Micromound.Sim;

// Demo run: charter a simulated mound, actuate with evidence, go "offline", expire the lease,
// reconnect and drain the chained backlog. This is a smoke script, not the test suite —
// authority rules are proven in tests/Micromound.Tests.

var now = DateTimeOffset.UtcNow;
var mound = new SimMound("mm-sim-001");

Console.WriteLine($"[{mound.MoundId}] state={mound.State}");

var charter = new Charter
{
    CharterId = Guid.NewGuid().ToString(),
    MoundId = mound.MoundId,
    MissionRef = "demo-mission",
    IssuedAt = now.ToString("O"),
    ExpiresAt = now.AddHours(1).ToString("O"),
    LeaseTtlSeconds = 900,
    ActionCeiling = "benign",
    Capabilities = ["sense.temp", "act.relay_1"],
    SafeState = "all_actuators_off"
};

var offer = mound.OfferCharter(charter, now);
Console.WriteLine($"charter accepted={offer.IsValid} state={mound.State}");

var record = mound.Actuate("act.relay_1", now);
Console.WriteLine($"actuate act.relay_1 -> {record.Outcome}, evidence={record.EvidenceRefs.Count}");

// Offline: lease runs down; no renewal possible from the device side.
var later = now.AddSeconds(charter.LeaseTtlSeconds + 1);
var quiesced = mound.QuiesceIfExpired(later);
Console.WriteLine($"lease expired -> quiesced={quiesced} state={mound.State}");

var refused = mound.Actuate("act.relay_1", later);
Console.WriteLine($"actuate after expiry -> {refused.Outcome}");

// Reconnect: drain backlog, verify the chain end-to-end like the colony would.
var backlog = mound.DrainUplink();
var chain = EnvelopeValidator.ValidateChain(backlog, "");
Console.WriteLine($"reconnect: {backlog.Count} envelopes, chain valid={chain.IsValid}");
