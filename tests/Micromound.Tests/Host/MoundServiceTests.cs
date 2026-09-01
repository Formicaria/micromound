using Micromound.Crypto;
using Micromound.Drivers;
using Micromound.Host;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The service lifecycle: the watchdog's physical response and a graceful, safe shutdown, driven by
/// an injected clock so the loop's safety behaviour is deterministic without a real timer. A shared
/// in-memory line, injected through the driver factory, lets a test see the hardware go safe.
/// </summary>
public sealed class MoundServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mm-svctest-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private static MoundManifest Manifest(string moundId)
    {
        var manifest = new MoundManifest { ManifestId = "mf", MoundId = moundId, IssuedAt = Now.ToWire(), SafeState = "all_actuators_off" };
        manifest.Hardware["soil"] = new HardwareBinding { Driver = "analog_sensor", Settings = new Dictionary<string, string> { ["capability"] = "sense.soil_moisture" } };
        manifest.Hardware["irrigation"] = new HardwareBinding
        {
            Driver = "digital_actuator",
            Settings = new Dictionary<string, string> { ["capability"] = "act.water_valve", ["max_on_s"] = "10", ["min_off_s"] = "300", ["max_rate_per_h"] = "6" }
        };
        manifest.Capabilities.Add("sense.soil_moisture");
        manifest.Capabilities.Add("act.water_valve");
        return manifest;
    }

    private static Charter Charter(string moundId) => new()
    {
        CharterId = "c-s", MoundId = moundId, MissionRef = "g", IssuedAt = Now.ToWire(), ExpiresAt = Now.AddHours(2).ToWire(),
        LeaseTtlSeconds = 900, ActionCeiling = "benign", Capabilities = ["sense.soil_moisture", "act.water_valve"],
        Limits = { ["act.water_valve"] = new CapabilityLimits { MaxOnSeconds = 25 } },
        Evidence = new EvidencePolicy { RequiredFor = ["act.*"], MinIntervalSeconds = 60 }, SafeState = "all_actuators_off"
    };

    private static Mission Watering(string moundId) => new()
    {
        MissionId = "ms-s", MoundId = moundId, CharterId = "c-s", RequiredCapabilities = ["sense.soil_moisture"],
        RequiredEvidence = ["b", "w"], SafeState = "all_actuators_off", ExpiresAt = Now.AddMinutes(30).ToWire(),
        Steps =
        {
            new MissionStep { StepId = "b", Op = MissionStepOps.Sense, Capability = "sense.soil_moisture", EvidenceTag = "b" },
            new MissionStep
            {
                StepId = "water", Op = MissionStepOps.Act, Capability = "act.water_valve", Parameters = { ["on_s"] = 5 },
                Condition = new StepCondition { SourceStep = "b", Op = ConditionOps.LessThan, Value = 20 }, EvidenceTag = "w"
            }
        }
    };

    private static DriverFactoryRegistry FactoriesWith(InMemoryDigitalOutput line)
    {
        var factories = new DriverFactoryRegistry();
        factories.Register(new AnalogSensorFactory());
        factories.Register(new DigitalActuatorFactory(() => line));   // one actuator: the test holds its line
        return factories;
    }

    private MoundHost Host(string moundId, InMemoryDigitalOutput line, Ed25519KeyPair keys, double heartbeat = 30) =>
        MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Manifest(moundId), StateDirectory = _dir,
            Drivers = FactoriesWith(line), GuardHeartbeatTimeoutSeconds = heartbeat
        });

    [Fact]
    public void A_watchdog_trip_drives_the_line_safe_and_the_kernel_refuses_actuation()
    {
        var line = new InMemoryDigitalOutput();
        var host = Host("mm-s1", line, Ed25519KeyPair.Generate());
        var service = new MoundService(host);
        host.Major.AcceptCharter(Charter("mm-s1"), Now);

        host.Cache.SaveAuthority(host.Authority);
        host.Guard.ReportTrip("interlock", "door open");
        line.Write(true);                    // pretend the line is energized
        service.Tick(Now);

        Assert.True(service.SafeStateEngaged);
        Assert.False(line.State);            // the tick drove it safe
        Assert.Equal("stopped", host.State); // a sticky trip escalates to a stop

        var report = host.ExecuteMission(Watering("mm-s1"), Now.AddSeconds(1));
        Assert.NotEqual(MissionStates.Completed, report.State);   // actuation refused under the trip
    }

    [Fact]
    public void A_safety_trip_survives_a_restart_as_a_stop()
    {
        // The regression the review caught: a trip lives only in memory, so without escalation a
        // reboot would clear it and re-enable actuation. Escalating it to a persisted stop fixes that.
        var keys = Ed25519KeyPair.Generate();
        var host = Host("mm-s1b", new InMemoryDigitalOutput(), keys);
        var service = new MoundService(host);
        host.Major.AcceptCharter(Charter("mm-s1b"), Now);
        host.Cache.SaveAuthority(host.Authority);

        host.Guard.ReportTrip("thermal", "over temperature");
        service.Tick(Now);   // escalates to a persisted stop

        var reborn = MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Manifest("mm-s1b"), StateDirectory = _dir,
            Drivers = FactoriesWith(new InMemoryDigitalOutput())
        });
        reborn.Restore(Now.AddSeconds(5));
        Assert.Equal("stopped", reborn.State);   // a reboot does NOT clear a safety trip
    }

    [Fact]
    public void A_stalled_loop_lets_the_heartbeat_go_stale_and_actuation_is_refused()
    {
        var host = Host("mm-s2", new InMemoryDigitalOutput(), Ed25519KeyPair.Generate(), heartbeat: 10);
        var service = new MoundService(host);
        host.Major.AcceptCharter(Charter("mm-s2"), Now);

        service.Tick(Now);   // last heartbeat at Now; then the loop "stalls" — no more ticks

        var report = host.ExecuteMission(Watering("mm-s2"), Now.AddSeconds(20));   // 20s > 10s timeout
        Assert.NotEqual(MissionStates.Completed, report.State);
    }

    [Fact]
    public void A_graceful_shutdown_is_safe_and_resumes_un_stopped()
    {
        var keys = Ed25519KeyPair.Generate();
        var line = new InMemoryDigitalOutput();
        var host = Host("mm-s3", line, keys);
        var service = new MoundService(host);
        host.Major.AcceptCharter(Charter("mm-s3"), Now);
        host.Cache.SaveAuthority(host.Authority);

        line.Write(true);
        service.Shutdown(Now);
        Assert.False(line.State);            // de-energized on shutdown

        // A restart resumes the persisted authority — a graceful shutdown is not a sticky stop.
        var reborn = MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Manifest("mm-s3"), StateDirectory = _dir,
            Drivers = FactoriesWith(new InMemoryDigitalOutput())
        });
        reborn.Restore(Now.AddSeconds(5));
        Assert.Equal("chartered", reborn.State);
    }
}
