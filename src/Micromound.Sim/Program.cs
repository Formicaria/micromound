using Micromound.Protocol;
using Micromound.Sim;

// Demo run: the whole loop, both ends.
//
// A controller enrolls a greenhouse mound, pushes configuration and a charter over the wire,
// assigns the documented watering mission, and the mound executes it through the six ants and
// reports back — evidence, records, mission report, all signed and chained. Then the wire goes
// down, the lease runs out, the mound quiesces, the process "restarts", and reconnection drains
// the backlog into a controller that verifies every byte before believing any of it.
//
// This is a smoke script, not the test suite — the rules are proven in tests/Micromound.Tests.

var now = DateTimeOffset.UtcNow;

var controller = new SimController();
var mound = new SimMound("mm-greenhouse-01")
{
    DeviceCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "sense.soil_moisture", "sense.temperature", "act.water_valve"
    },
    FirmwareLimits = new Dictionary<string, CapabilityLimits>(StringComparer.Ordinal)
    {
        ["act.water_valve"] = new CapabilityLimits { MaxOnSeconds = 30, MinOffSeconds = 300, MaxRatePerHour = 6 }
    }
};

var link = mound.ConnectTo(controller);
Console.WriteLine($"[{mound.MoundId}] enrolled; state={mound.State}");

// The fake world: dry soil that watering moistens.
mound.Sensor("sense.soil_moisture").Reading = 17;
mound.Sensor("sense.temperature").Reading = 24;
mound.Relay("act.water_valve").OnActuated = onSeconds =>
    mound.Sensor("sense.soil_moisture").Reading += onSeconds * 1.5;

// The controller configures and charters the mound — over the wire, not by method call.
controller.PushConfig(new MoundManifest
{
    ManifestId = Guid.NewGuid().ToString(),
    MoundId = mound.MoundId,
    IssuedAt = now.ToWire(),
    Capabilities = ["sense.soil_moisture", "sense.temperature", "act.water_valve"],
    DeviceLimits = { ["act.water_valve"] = new CapabilityLimits { MaxOnSeconds = 20 } },
    SafeState = "all_actuators_off"
}, now);

controller.IssueCharter(new Charter
{
    CharterId = Guid.NewGuid().ToString(),
    MoundId = mound.MoundId,
    MissionRef = "greenhouse-watering",
    IssuedAt = now.ToWire(),
    ExpiresAt = now.AddHours(1).ToWire(),
    LeaseTtlSeconds = 900,
    ActionCeiling = "benign",
    Capabilities = ["sense.soil_moisture", "sense.temperature", "act.water_valve"],
    Limits = { ["act.water_valve"] = new CapabilityLimits { MaxOnSeconds = 10 } },
    Evidence = new EvidencePolicy { RequiredFor = ["act.*"], MinIntervalSeconds = 60 },
    SafeState = "all_actuators_off"
}, now);

var sync = mound.Sync(now);
Console.WriteLine($"sync: sent={sync.EnvelopesSent} downlink={sync.DownlinkHandled} state={mound.State}");

// The documented mission, assigned over the wire: inspect, water only if necessary, prove it.
controller.AssignMission(new Mission
{
    MissionId = "ms-demo-001",
    MoundId = mound.MoundId,
    CharterId = mound.Authority.ActiveCharter!.CharterId,
    RequiredCapabilities = ["sense.soil_moisture", "sense.temperature"],
    RequiredEvidence = ["soil_before", "watering_action", "soil_after"],
    SafeState = "all_actuators_off",
    ExpiresAt = now.AddMinutes(30).ToWire(),
    Steps =
    [
        new MissionStep { StepId = "soil_before", Op = MissionStepOps.Sense,
            Capability = "sense.soil_moisture", EvidenceTag = "soil_before" },
        new MissionStep { StepId = "temp", Op = MissionStepOps.Sense, Capability = "sense.temperature" },
        new MissionStep { StepId = "water", Op = MissionStepOps.Act, Capability = "act.water_valve",
            Parameters = { ["on_s"] = 10 },
            Condition = new StepCondition { SourceStep = "soil_before", Op = ConditionOps.LessThan, Value = 20 },
            EvidenceTag = "watering_action" },
        new MissionStep { StepId = "soil_after", Op = MissionStepOps.Verify,
            Capability = "sense.soil_moisture", Confirms = "water", EvidenceTag = "soil_after" }
    ]
}, now.AddSeconds(15));

sync = mound.Sync(now.AddSeconds(15));
var account = controller.Account(mound.MoundId);
Console.WriteLine($"mission over the wire -> reports={account.Reports.Count} " +
                  $"verdict={account.Reports.LastOrDefault()?.State} " +
                  $"soil now {mound.Sensor("sense.soil_moisture").Reading}");

sync = mound.Sync(now.AddSeconds(20));
Console.WriteLine($"records at controller: {controller.Account(mound.MoundId).Records.Count}, " +
                  $"evidence items: {account.Evidence.Count}, chain refusals: {account.Refusals}");

// The wire goes down. Work continues under the lease it already had — and no further.
link.Online = false;
var offlineActuation = mound.Actuate("act.water_valve", now.AddSeconds(400), 5);
Console.WriteLine($"offline actuation -> {offlineActuation.Outcome}");

var later = now.AddSeconds(915 + 15);
Console.WriteLine($"lease expired -> quiesced={mound.QuiesceIfExpired(later)} state={mound.State}");
Console.WriteLine($"actuate after expiry -> {mound.Actuate("act.water_valve", later, 5).Detail}");

// "Restart" the device: same store, same keys, new process.
var reborn = new SimMound(mound.MoundId) { Keys = mound.Keys, Store = mound.Store,
    DeviceCapabilities = mound.DeviceCapabilities, FirmwareLimits = mound.FirmwareLimits };
reborn.Restore(later);
Console.WriteLine($"after restart -> state={reborn.State} (a restart revives nothing)");

// Reconnect: the backlog drains into a controller that verifies chain and signatures.
var rebornLink = reborn.ConnectTo(controller);
_ = rebornLink;
sync = reborn.Sync(later.AddSeconds(5));
account = controller.Account(mound.MoundId);
Console.WriteLine($"reconnect: delivered={sync.Delivered} sent={sync.EnvelopesSent}, " +
                  $"controller refusals={account.Refusals} (0 means the chain held)");

Console.WriteLine("resumption is never implicit: state=" + reborn.State +
                  " until the controller issues a fresh charter");
