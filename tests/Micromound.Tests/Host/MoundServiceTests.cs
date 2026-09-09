using System.Diagnostics;
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

    /// <summary>
    /// A manifest with TWO actuators, so a test can prove that one failing driver does not decide the
    /// fate of the other. One actuator is not enough to see the bug this covers.
    /// </summary>
    private static MoundManifest TwoActuatorManifest(string moundId)
    {
        var manifest = new MoundManifest { ManifestId = "mf2", MoundId = moundId, IssuedAt = Now.ToWire(), SafeState = "all_actuators_off" };
        manifest.Hardware["first"] = new HardwareBinding
        {
            Driver = "digital_actuator",
            Settings = new Dictionary<string, string> { ["capability"] = "act.first", ["max_on_s"] = "10" }
        };
        manifest.Hardware["second"] = new HardwareBinding
        {
            Driver = "digital_actuator",
            Settings = new Dictionary<string, string> { ["capability"] = "act.second", ["max_on_s"] = "10" }
        };
        manifest.Capabilities.Add("act.first");
        manifest.Capabilities.Add("act.second");
        return manifest;
    }

    /// <summary>Two named lines, resolved in manifest order by the capability each is bound to.</summary>
    private static DriverFactoryRegistry FactoriesWithPair(IDigitalOutput first, IDigitalOutput second)
    {
        var factories = new DriverFactoryRegistry();
        factories.Register(new AnalogSensorFactory());
        factories.Register(new DigitalActuatorFactory(settings =>
            settings.TryGetValue("capability", out var capability) && capability == "act.first" ? first : second));
        return factories;
    }

    /// <summary>
    /// A line that will not de-energize once hot — the driver failure a safe-state walk must survive.
    /// The initial safe write at bring-up (while already low) succeeds, exactly as a real line's does,
    /// so composition still comes up; only a low write while HIGH throws.
    /// </summary>
    private sealed class RefusesToGoSafe : IDigitalOutput
    {
        public bool State { get; private set; }
        public void Write(bool high)
        {
            if (!high && State) throw new IOException("simulated: this line will not go safe");
            State = high;
        }
    }

    /// <summary>
    /// A line that HANGS rather than throwing when asked to go safe — P0.8's other half. Like
    /// <see cref="RefusesToGoSafe"/> the bring-up write (low while already low) returns, so
    /// composition comes up; only a low write while HIGH blocks. The test releases it at the end so
    /// no thread-pool thread is stranded for the rest of the run.
    /// </summary>
    private sealed class BlocksGoingSafe : IDigitalOutput, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        public bool State { get; private set; }
        public int Calls { get; private set; }

        public void Write(bool high)
        {
            if (!high && State)
            {
                Calls++;
                _release.Wait();          // never returns until the test says so
                return;
            }
            State = high;
        }

        public void Dispose() { _release.Set(); _release.Dispose(); }
    }

    /// <summary>An ordinary line that also remembers which kind of thread was used to make it safe.</summary>
    private sealed class RecordsItsThread : IDigitalOutput
    {
        public bool State { get; private set; }
        public bool? RanOnThreadPool { get; private set; }

        public void Write(bool high)
        {
            if (!high && State) RanOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
            State = high;
        }
    }

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
    /// <summary>
    /// A monotonic source a test moves by hand. Only <see cref="GetTimestamp"/> and the frequency need
    /// overriding — <c>GetElapsedTime(stamp)</c> is computed from them — so "time passed" is just
    /// <see cref="Advance"/>, called by whatever is pretending to block.
    /// </summary>
    private sealed class FakeMonotonic : TimeProvider
    {
        private long _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    /// <summary>
    /// A transport whose exchange really costs time — the slow TLS round trip, the DNS stall, the
    /// timeout — expressed as monotonic ticks the rest of the tick can then account for.
    /// </summary>
    private sealed class SlowTransport(FakeMonotonic clock, TimeSpan cost) : ISyncTransport
    {
        public int Exchanges { get; private set; }
        public bool TryExchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail)
        {
            Exchanges++;
            clock.Advance(cost);
            downlink = [];
            detail = "slow";
            return false;   // offline: the queue keeps its records, and the tick continues
        }
    }

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

    /// <remarks>
    /// A lease is a promise about TIME, so it runs out on an idle mound exactly as it does on a busy
    /// one — and the mound has to notice by itself, because the scenario the lease exists for is the
    /// one where nobody is left to tell it. Before this the expiry was only ever checked when a
    /// mission arrived, so a mound nobody asked anything sat 'chartered' with its line hot for as
    /// long as the silence lasted. Nothing in this test asks the mound to do anything: it is a
    /// service tick and a clock.
    /// </remarks>
    [Fact]
    public void An_idle_tick_past_the_lease_quiesces_the_mound_and_de_energizes_it()
    {
        var line = new InMemoryDigitalOutput();
        var host = Host("mm-s5", line, Ed25519KeyPair.Generate());
        var service = new MoundService(host);
        host.Major.AcceptCharter(Charter("mm-s5"), Now);
        line.Write(true);

        var expiry = host.Authority.LeaseExpiresAt;
        service.Tick(expiry.AddSeconds(-1));
        Assert.Equal("chartered", host.State);   // inside the lease, nothing changes

        service.Tick(expiry.AddSeconds(1));

        Assert.Equal("quiesced", host.State);
        Assert.False(line.State);
    }

    /// <summary>And it is durable: a restart comes back quiesced, not briefly re-authorized.</summary>
    [Fact]
    public void The_quiesce_an_idle_tick_reached_survives_a_restart()
    {
        var keys = Ed25519KeyPair.Generate();
        var host = Host("mm-s6", new InMemoryDigitalOutput(), keys);
        var service = new MoundService(host);
        host.Major.AcceptCharter(Charter("mm-s6"), Now);
        var after = host.Authority.LeaseExpiresAt.AddSeconds(1);

        service.Tick(after);

        var reborn = MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Manifest("mm-s6"), StateDirectory = _dir,
            Drivers = FactoriesWith(new InMemoryDigitalOutput())
        });
        reborn.Restore(after);

        Assert.Equal("quiesced", reborn.State);
    }

    /// <remarks>
    /// **The safe-state walk is per driver, on every path that reaches it** (`v0.9.29`, roadmap P0.8).
    ///
    /// `MoundHost.EnterSafeState()` was always isolated — try/catch per driver, a reported trip, under
    /// the safe gate. But `WatchingForSafeState`, the path a stop, a quiesce or an expired lease
    /// actually takes, walked the drivers itself in a bare `foreach`. The first driver to throw ended
    /// the walk, so every driver after it stayed energized *during a stop*, no trip was recorded, and
    /// the exception escaped into whichever caller triggered the transition.
    ///
    /// This is the regression check: two actuators, the first refusing to go safe, and the second must
    /// still be de-energized when the stop lands.
    /// </remarks>
    [Fact]
    public void A_driver_that_refuses_to_go_safe_does_not_keep_the_others_energized()
    {
        var stubborn = new RefusesToGoSafe();
        var ordinary = new InMemoryDigitalOutput();
        var host = MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(), Manifest = TwoActuatorManifest("mm-s7"),
            StateDirectory = _dir, Drivers = FactoriesWithPair(stubborn, ordinary)
        });

        stubborn.Write(true);
        ordinary.Write(true);

        host.Stop();                       // the transition every stop takes

        Assert.False(ordinary.State);      // the second line went safe despite the first throwing
        Assert.True(host.Guard.HasTrip);   // and the failure was recorded, not swallowed
    }

    /// <summary>The same guarantee on the path an expired lease takes — the one `v0.9.27` added.</summary>
    [Fact]
    public void An_expired_lease_de_energizes_every_driver_it_can_even_when_one_throws()
    {
        var stubborn = new RefusesToGoSafe();
        var ordinary = new InMemoryDigitalOutput();
        var host = MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(), Manifest = TwoActuatorManifest("mm-s8"),
            StateDirectory = _dir, Drivers = FactoriesWithPair(stubborn, ordinary)
        });
        host.Major.AcceptCharter(TwoActuatorCharter("mm-s8"), Now);

        stubborn.Write(true);
        ordinary.Write(true);

        // An idle tick past the lease. Nothing is asking this mound to do anything.
        new MoundService(host).Tick(host.Authority.LeaseExpiresAt.AddSeconds(1));

        // The line that could go safe did, even though the walk began with one that could not.
        Assert.False(ordinary.State);
        Assert.True(host.Guard.HasTrip);

        // And the mound ends up STOPPED rather than merely quiesced: the driver that would not
        // de-energize is a trip, and the tick's own watchdog response escalates a trip to a
        // persisted stop. That is the stricter of the two states and the correct one — a mound that
        // cannot prove its hardware is safe must be treated as unsafe, and a restart never clears it.
        Assert.Equal("stopped", host.State);
    }

    private static Charter TwoActuatorCharter(string moundId) => new()
    {
        CharterId = "c-s", MoundId = moundId, MissionRef = "g", IssuedAt = Now.ToWire(),
        ExpiresAt = Now.AddHours(2).ToWire(), LeaseTtlSeconds = 900, ActionCeiling = "benign",
        Capabilities = ["act.first", "act.second"],
        Evidence = new EvidencePolicy { RequiredFor = ["act.*"], MinIntervalSeconds = 60 },
        SafeState = "all_actuators_off"
    };

    // ---- P0.2: a deadline a slow sync or a stepped clock cannot stretch (v0.9.31) --------------

    /// <remarks>
    /// The tick used to take ONE `UtcNow` at the top and reuse it for the hold release at the bottom,
    /// with a blocking network exchange in between. So the time the sync cost was time a held line
    /// never saw: a 5 s hold, a tick one second in, and a 10 s exchange left the line hot with
    /// eleven seconds elapsed — and it stayed hot until some later tick's timestamp happened to pass
    /// the deadline.
    ///
    /// The fix accounts for what the blocking call actually cost, measured monotonically and added
    /// to the caller's clock. This test is the difference: the same single tick now releases.
    /// </remarks>
    [Fact]
    public void A_slow_sync_does_not_buy_a_held_line_extra_time()
    {
        var clock = new FakeMonotonic();
        var line = new InMemoryDigitalOutput();
        var factories = new DriverFactoryRegistry();
        factories.Register(new AnalogSensorFactory());
        factories.Register(new DigitalActuatorFactory(() => line, clock));

        var transport = new SlowTransport(clock, TimeSpan.FromSeconds(10));
        var host = MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(), Manifest = Manifest("mm-s9"), StateDirectory = _dir,
            Drivers = factories, Transport = transport, GuardHeartbeatTimeoutSeconds = 0
        });
        var service = new MoundService(host, clock);
        host.Major.AcceptCharter(Charter("mm-s9"), Now);

        host.ExecuteMission(Watering("mm-s9"), Now);    // 5 s hold, deadline Now+5
        Assert.True(line.State);

        // One tick, one second in. The exchange inside it costs ten seconds.
        service.Tick(Now.AddSeconds(1));

        Assert.Equal(1, transport.Exchanges);
        Assert.False(line.State);   // eleven seconds really passed; the line is down
    }

    /// <remarks>
    /// The other half: a hold is bounded by a duration, and a wall clock can be stepped. An NTP
    /// correction that jumps backwards must not postpone a release that is physically already due,
    /// so the hold carries a monotonic deadline as well and fires on whichever says it is due first.
    /// Earliest-wins is the fail-safe direction — releasing an output early is safe; holding one late
    /// is the failure the hold exists to bound.
    /// </remarks>
    [Fact]
    public void A_wall_clock_stepped_backwards_cannot_extend_a_hold()
    {
        var clock = new FakeMonotonic();
        var line = new InMemoryDigitalOutput();
        var factories = new DriverFactoryRegistry();
        factories.Register(new AnalogSensorFactory());
        factories.Register(new DigitalActuatorFactory(() => line, clock));

        var host = MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(), Manifest = Manifest("mm-s10"), StateDirectory = _dir,
            Drivers = factories, GuardHeartbeatTimeoutSeconds = 0
        });
        host.Major.AcceptCharter(Charter("mm-s10"), Now);

        host.ExecuteMission(Watering("mm-s10"), Now);   // 5 s hold
        Assert.True(line.State);

        clock.Advance(TimeSpan.FromSeconds(6));         // six seconds really elapsed

        // ...and then the clock is corrected an hour backwards. On the wall clock the hold is
        // nowhere near due; it is due all the same.
        host.ServiceActuations(Now.AddHours(-1));

        Assert.False(line.State);
    }

    /// <summary>And neither clock saying it is due still means it is not due — no early release.</summary>
    [Fact]
    public void A_hold_neither_clock_calls_due_is_not_released()
    {
        var clock = new FakeMonotonic();
        var line = new InMemoryDigitalOutput();
        var factories = new DriverFactoryRegistry();
        factories.Register(new AnalogSensorFactory());
        factories.Register(new DigitalActuatorFactory(() => line, clock));

        var host = MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(), Manifest = Manifest("mm-s11"), StateDirectory = _dir,
            Drivers = factories, GuardHeartbeatTimeoutSeconds = 0
        });
        host.Major.AcceptCharter(Charter("mm-s11"), Now);

        host.ExecuteMission(Watering("mm-s11"), Now);
        clock.Advance(TimeSpan.FromSeconds(2));
        host.ServiceActuations(Now.AddSeconds(2));

        Assert.True(line.State);
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

    // ---- P0.8: a driver that BLOCKS, not one that throws (v0.9.39) ------------------------------

    /// <remarks>
    /// `v0.9.29` isolated the driver that THROWS. This is the other half, and it is the one that
    /// cannot be caught: a driver that never returns stops the safe-state walk at itself, so every
    /// driver after it in the manifest stays energized — while the caller holds the safe-state gate,
    /// so the independent watchdog cannot get in either. One stuck I2C transaction keeps a pump
    /// running.
    ///
    /// <para>Nothing here interrupts the stuck driver or makes ITS line safe; that is not possible.
    /// What is possible is to stop WAITING for it, which is what the other line's state proves.</para>
    /// </remarks>
    [Fact]
    public void A_driver_that_blocks_going_safe_does_not_keep_the_others_energized()
    {
        using var stuck = new BlocksGoingSafe();
        var ordinary = new RecordsItsThread();
        var host = MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(), Manifest = TwoActuatorManifest("mm-s20"),
            StateDirectory = _dir, Drivers = FactoriesWithPair(stuck, ordinary),
            SafeStateTimeoutSeconds = 0.2
        });

        stuck.Write(true);
        ordinary.Write(true);

        var started = Stopwatch.StartNew();
        host.Stop();
        started.Stop();

        Assert.False(ordinary.State);                              // the second line went safe anyway
        Assert.True(host.Guard.HasTrip);                           // and the stuck one was reported, not swallowed

        // And it went safe on a thread of its own rather than one borrowed from the pool. `v0.9.39`
        // used Task.Run, so the blocked driver held a pool thread and the next driver's call waited
        // for the pool to inject another — about a second — which on a two-core box is longer than
        // the bound. The second driver timed out too, and its line stayed live.
        //
        // This single assertion is the whole regression, and it is deliberately the ONLY one.
        // `v0.9.42` also shipped a test that saturated the thread pool to reproduce the starvation
        // directly; it worked, and it broke an unrelated test, because xunit runs collections in
        // parallel and the pool is process-global. A test may not damage a shared resource to make
        // its point when a property of the mechanism says the same thing for nothing (`v0.9.44`).
        Assert.Equal(false, ordinary.RanOnThreadPool);

        // The gate is released in bounded time, which is what lets the independent watchdog take it.
        // Generous, because a loaded CI box schedules the pool thread when it feels like it — the
        // claim under test is "bounded", not "prompt".
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10),
            $"the safe-state walk took {started.Elapsed.TotalSeconds:0.##}s; it must not wait on a stuck driver");
    }

    /// <remarks>
    /// The same guarantee on the path an expired lease takes, and with the escalation the trip earns:
    /// a driver that will not go safe is a trip, and the tick's own watchdog response turns a trip
    /// into a persisted stop. An idle mound, nobody asking it for anything, one wedged driver — and it
    /// still ends up stopped with every line it could reach de-energized.
    /// </remarks>
    [Fact]
    public void An_expired_lease_still_de_energizes_what_it_can_when_a_driver_blocks()
    {
        using var stuck = new BlocksGoingSafe();
        var ordinary = new InMemoryDigitalOutput();
        var host = MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(), Manifest = TwoActuatorManifest("mm-s21"),
            StateDirectory = _dir, Drivers = FactoriesWithPair(stuck, ordinary),
            SafeStateTimeoutSeconds = 0.2
        });
        host.Major.AcceptCharter(TwoActuatorCharter("mm-s21"), Now);

        stuck.Write(true);
        ordinary.Write(true);

        new MoundService(host).Tick(host.Authority.LeaseExpiresAt.AddSeconds(1));

        Assert.False(ordinary.State);
        Assert.Equal("stopped", host.State);
    }

    /// <remarks>
    /// A driver that has already spent its bound is not called again. It has proved it does not
    /// answer, the mound is stopping because of it, and every further attempt would strand another
    /// thread-pool thread — on a mound that goes safe on every tick, that is a leak with no ceiling.
    /// The second walk therefore has to be immediate as well as harmless.
    /// </remarks>
    [Fact]
    public void A_driver_that_has_been_abandoned_is_not_called_again()
    {
        using var stuck = new BlocksGoingSafe();
        var ordinary = new InMemoryDigitalOutput();
        var host = MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(), Manifest = TwoActuatorManifest("mm-s22"),
            StateDirectory = _dir, Drivers = FactoriesWithPair(stuck, ordinary),
            SafeStateTimeoutSeconds = 0.2
        });

        stuck.Write(true);
        ordinary.Write(true);
        host.EnterSafeState();
        Assert.Equal(1, stuck.Calls);

        ordinary.Write(true);
        var again = Stopwatch.StartNew();
        host.EnterSafeState();
        again.Stop();

        Assert.Equal(1, stuck.Calls);                              // never asked a second time
        Assert.False(ordinary.State);                              // and the rest of the walk still ran
        Assert.True(again.Elapsed < TimeSpan.FromSeconds(1),
            $"the second walk waited {again.Elapsed.TotalSeconds:0.##}s on a driver already known not to answer");
    }

}
