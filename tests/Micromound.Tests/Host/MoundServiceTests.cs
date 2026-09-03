using Micromound.Crypto;
using Micromound.Drivers;
using Micromound.Host;
using Micromound.Protocol;
using Micromound.Sync;
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

    private static DriverFactoryRegistry FactoriesWith(IDigitalOutput line)
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
    public void A_timed_actuation_holds_the_line_and_a_later_tick_releases_it()
    {
        var line = new InMemoryDigitalOutput();
        var host = Host("mm-s4", line, Ed25519KeyPair.Generate());
        var service = new MoundService(host);
        host.Major.AcceptCharter(Charter("mm-s4"), Now);
        host.Cache.SaveAuthority(host.Authority);
        service.Tick(Now);   // beat, so the heartbeat is fresh enough to actuate

        host.ExecuteMission(Watering("mm-s4"), Now);   // waters for on_s = 5s
        Assert.True(line.State);                        // HELD active — a real valve is open for its duration

        service.Tick(Now.AddSeconds(2));                // before the deadline: still held
        Assert.True(line.State);

        service.Tick(Now.AddSeconds(5));                // the sweep releases the line at the deadline
        Assert.False(line.State);
    }

    [Fact]
    public void A_hold_the_hardware_cannot_release_escalates_to_a_persisted_stop()
    {
        // The safety flip side of a timed hold: the line is deliberately held hot, so a line that will
        // not release is a fault that must escalate — the tick sweep trips it, and the trip becomes a
        // persisted stop (a restart never clears it).
        var keys = Ed25519KeyPair.Generate();
        var line = new ThrowsOnRelease();
        var host = MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Manifest("mm-s6"), StateDirectory = _dir,
            Drivers = FactoriesWith(line), GuardHeartbeatTimeoutSeconds = 30
        });
        var service = new MoundService(host);
        host.Major.AcceptCharter(Charter("mm-s6"), Now);
        host.Cache.SaveAuthority(host.Authority);
        service.Tick(Now);   // beat

        host.ExecuteMission(Watering("mm-s6"), Now);   // energize succeeds; the line is held
        Assert.True(line.State);

        service.Tick(Now.AddSeconds(5));               // deadline: release throws -> trip -> stop
        Assert.Equal("stopped", host.State);

        // The stop is durable: a reboot does not clear it (and a reborn host over a working line stays stopped).
        var reborn = MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Manifest("mm-s6"), StateDirectory = _dir,
            Drivers = FactoriesWith(new InMemoryDigitalOutput())
        });
        reborn.Restore(Now.AddSeconds(10));
        Assert.Equal("stopped", reborn.State);
    }

    [Fact]
    public void The_watchdog_stop_de_energizes_a_held_line_stops_and_persists()
    {
        // The independent watchdog's action, exercised directly: a mission has left a line held hot,
        // the loop is (imagined) hung, and WatchdogStop must drive the line safe, halt, and make the
        // stop durable — the whole reason the watchdog exists now that actuations are held.
        var keys = Ed25519KeyPair.Generate();
        var line = new InMemoryDigitalOutput();
        var host = Host("mm-wd", line, keys);
        var service = new MoundService(host);
        host.Major.AcceptCharter(Charter("mm-wd"), Now);
        host.Cache.SaveAuthority(host.Authority);
        service.Tick(Now);

        host.ExecuteMission(Watering("mm-wd"), Now);   // waters: the line is held active
        Assert.True(line.State);

        host.WatchdogStop("service loop unresponsive");
        Assert.False(line.State);             // de-energized
        Assert.Equal("stopped", host.State);  // halted
        Assert.True(host.Guard.HasTrip);      // a recorded, auditable reason

        // Durable: a reborn host over a working line stays stopped.
        var reborn = MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Manifest("mm-wd"), StateDirectory = _dir,
            Drivers = FactoriesWith(new InMemoryDigitalOutput())
        });
        reborn.Restore(Now.AddSeconds(5));
        Assert.Equal("stopped", reborn.State);
    }

    [Fact]
    public void A_resumed_loop_observes_the_watchdog_stop_before_it_could_actuate_again()
    {
        // The concurrency case: the watchdog fired on its own thread (here, a direct call) while this
        // loop was stuck. When the loop resumes and ticks, it must observe the stop at the TOP of the
        // tick — before sync could authorize an actuation — and stay stopped.
        var line = new InMemoryDigitalOutput();
        var host = Host("mm-wd2", line, Ed25519KeyPair.Generate());
        var service = new MoundService(host);
        host.Major.AcceptCharter(Charter("mm-wd2"), Now);
        host.Cache.SaveAuthority(host.Authority);
        service.Tick(Now);

        host.WatchdogStop("service loop unresponsive");   // fired while the loop was "hung"

        service.Tick(Now.AddSeconds(1));                  // the loop resumes and ticks
        Assert.Equal("stopped", host.State);              // honoured the stop, did not carry on

        var report = host.ExecuteMission(Watering("mm-wd2"), Now.AddSeconds(2));
        Assert.NotEqual(MissionStates.Completed, report.State);   // actuation refused under the stop
        Assert.False(line.State);
    }

    private MoundHost HostWithTransport(string moundId, IDigitalOutput line, ISyncTransport transport) =>
        MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(), Manifest = Manifest(moundId), StateDirectory = _dir,
            Drivers = FactoriesWith(line), GuardHeartbeatTimeoutSeconds = 30, Transport = transport
        });

    [Fact]
    public void The_charters_sync_cadence_throttles_sync_only_and_never_delays_a_hold_release()
    {
        // The live cadence is the active charter's sync_interval_s (PROTOCOL.md §4) — the controller
        // sets it and judges the mound offline from it. The safety property of honouring it: a
        // controller asking to hear from the mound every 60 s is NOT asking for a 5 s valve hold to be
        // released 60 s late. The tick keeps its own rhythm for hold release; only the sync is throttled.
        var line = new InMemoryDigitalOutput();
        var transport = new CountingTransport();
        var host = HostWithTransport("mm-cad", line, transport);
        var service = new MoundService(host);
        var charter = Charter("mm-cad");
        charter.SyncIntervalSeconds = 60;
        host.Major.AcceptCharter(charter, Now);
        host.Cache.SaveAuthority(host.Authority);

        service.Tick(Now);                                 // first tick always syncs
        Assert.Equal(1, transport.Exchanges);
        Assert.Equal(TimeSpan.FromSeconds(60), service.EffectiveSyncInterval);

        host.ExecuteMission(Watering("mm-cad"), Now);      // holds the line for 5 s
        Assert.True(line.State);

        service.Tick(Now.AddSeconds(5));                   // 5 s later: hold released, but NOT yet time to sync
        Assert.False(line.State);                          // released on the tick's rhythm
        Assert.Equal(1, transport.Exchanges);              // sync still throttled

        service.Tick(Now.AddSeconds(30));
        Assert.Equal(1, transport.Exchanges);              // still inside the 60 s cadence

        service.Tick(Now.AddSeconds(60));
        Assert.Equal(2, transport.Exchanges);              // cadence elapsed: synced
    }

    [Fact]
    public void Before_any_charter_the_enrollment_cadence_is_the_bootstrap()
    {
        // Enrollment's sync_interval_s applies until a charter arrives.
        var transport = new CountingTransport();
        var host = HostWithTransport("mm-boot", new InMemoryDigitalOutput(), transport);
        var service = new MoundService(host) { SyncInterval = TimeSpan.FromSeconds(60) };

        service.Tick(Now);
        service.Tick(Now.AddSeconds(30));
        Assert.Equal(1, transport.Exchanges);              // throttled by the enrollment cadence
        service.Tick(Now.AddSeconds(60));
        Assert.Equal(2, transport.Exchanges);
    }

    [Fact]
    public void An_arriving_charter_takes_over_the_cadence_from_enrollment()
    {
        // The charter is the fresher copy of the same controller setting: once chartered, it wins.
        var transport = new CountingTransport();
        var host = HostWithTransport("mm-take", new InMemoryDigitalOutput(), transport);
        var service = new MoundService(host) { SyncInterval = TimeSpan.FromSeconds(60) };
        var charter = Charter("mm-take");
        charter.SyncIntervalSeconds = 20;
        host.Major.AcceptCharter(charter, Now);

        Assert.Equal(TimeSpan.FromSeconds(20), service.EffectiveSyncInterval);
        service.Tick(Now);
        service.Tick(Now.AddSeconds(20));
        Assert.Equal(2, transport.Exchanges);              // synced at the charter's 20 s, not enrollment's 60 s
    }

    [Fact]
    public void With_no_controller_cadence_every_tick_syncs_as_before()
    {
        var transport = new CountingTransport();
        var host = HostWithTransport("mm-nocad", new InMemoryDigitalOutput(), transport);
        var service = new MoundService(host);              // no charter, no enrollment cadence: the prior behaviour

        service.Tick(Now);
        service.Tick(Now.AddSeconds(5));
        service.Tick(Now.AddSeconds(10));
        Assert.Equal(3, transport.Exchanges);
    }

    /// <summary>A transport that counts exchanges and answers "offline" — enough to observe the cadence.</summary>
    private sealed class CountingTransport : ISyncTransport
    {
        public int Exchanges { get; private set; }
        public bool TryExchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail)
        {
            Exchanges++;
            downlink = [];
            detail = "counting";
            return false;
        }
    }

    /// <summary>A line that refuses to de-energize once hot — the physical failure a held line must
    /// survive. The initial safe write (while already low) succeeds; a low write while high throws.</summary>
    private sealed class ThrowsOnRelease : IDigitalOutput
    {
        public bool State { get; private set; }
        public void Write(bool high)
        {
            if (!high && State)
                throw new IOException("simulated stuck line: cannot de-energize");
            State = high;
        }
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
