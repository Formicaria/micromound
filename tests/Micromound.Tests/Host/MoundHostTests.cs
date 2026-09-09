using Micromound.Capabilities;
using Micromound.Crypto;
using Micromound.Host;
using Micromound.Protocol;
using Micromound.Runtime;
using Micromound.Sim;
using Micromound.Sync;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The runnable host: a mound composed from a manifest over a durable file store, using the same
/// <see cref="MoundComposition"/> the simulator uses. These prove the manifest → generic drivers →
/// kernel → ants → mission path a real Pi will run, that bring-up fails closed, and that the v0.9.1
/// recovery semantics hold over the real on-disk store.
/// </summary>
public sealed class MoundHostTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mm-hosttest-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private static MoundManifest Greenhouse(string moundId)
    {
        var manifest = new MoundManifest
        {
            ManifestId = "mf-1",
            MoundId = moundId,
            IssuedAt = Now.ToWire(),
            SafeState = "all_actuators_off"
        };
        manifest.Hardware["soil"] = new HardwareBinding
        {
            Driver = "analog_sensor",
            Settings = new Dictionary<string, string> { ["capability"] = "sense.soil_moisture", ["unit"] = "pct" }
        };
        manifest.Hardware["irrigation"] = new HardwareBinding
        {
            Driver = "digital_actuator",
            Settings = new Dictionary<string, string>
            {
                ["capability"] = "act.water_valve", ["max_on_s"] = "10", ["min_off_s"] = "300", ["max_rate_per_h"] = "6"
            }
        };
        manifest.Capabilities.Add("sense.soil_moisture");
        manifest.Capabilities.Add("act.water_valve");
        return manifest;
    }

    private static Charter Charter(string moundId) => new()
    {
        CharterId = "c-host",
        MoundId = moundId,
        MissionRef = "greenhouse",
        IssuedAt = Now.ToWire(),
        ExpiresAt = Now.AddHours(2).ToWire(),
        LeaseTtlSeconds = 900,
        ActionCeiling = "benign",
        Capabilities = ["sense.soil_moisture", "act.water_valve"],
        Limits = { ["act.water_valve"] = new CapabilityLimits { MaxOnSeconds = 25 } },
        Evidence = new EvidencePolicy { RequiredFor = ["act.*"], MinIntervalSeconds = 60 },
        SafeState = "all_actuators_off"
    };

    private static Mission Watering(string moundId) => new()
    {
        MissionId = "ms-host",
        MoundId = moundId,
        CharterId = "c-host",
        RequiredCapabilities = ["sense.soil_moisture"],
        RequiredEvidence = ["soil_before", "watering"],
        SafeState = "all_actuators_off",
        ExpiresAt = Now.AddMinutes(30).ToWire(),
        Steps =
        {
            new MissionStep { StepId = "soil_before", Op = MissionStepOps.Sense, Capability = "sense.soil_moisture", EvidenceTag = "soil_before" },
            new MissionStep
            {
                StepId = "water", Op = MissionStepOps.Act, Capability = "act.water_valve",
                Parameters = { ["on_s"] = 60 },
                Condition = new StepCondition { SourceStep = "soil_before", Op = ConditionOps.LessThan, Value = 20 },
                EvidenceTag = "watering"
            }
        }
    };

    [Fact]
    public void A_manifest_composes_into_a_running_mound()
    {
        var host = MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(),
            Manifest = Greenhouse("mm-host-01"),
            StateDirectory = _dir
        });

        Assert.Equal("observe_only", host.State);
        Assert.Contains("act.water_valve", host.Kernel.Capabilities.DeclaredCapabilities());
        Assert.True(Directory.Exists(Path.Combine(_dir, "state")));   // durable state directory created
    }

    /// <remarks>
    /// P0.7, `v0.9.37`: the kernel's check 14 is only as real as the composition that attaches it.
    /// A kernel with no audit path wired skips the check entirely — the right default for one built
    /// with no queue behind it — so a regression in the wiring would be invisible in every other
    /// test, including the fixture that pins the rule itself.
    /// </remarks>
    [Fact]
    public void A_composed_host_wires_its_uplink_queue_to_the_kernels_audit_check()
    {
        var host = MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(),
            Manifest = Greenhouse("mm-host-01"),
            StateDirectory = _dir
        });

        Assert.NotNull(host.Kernel.Audit);
        Assert.True(host.Kernel.Audit!.CapacityForNewWork > 0,
            "a real mound must present a bound to check 14; zero or less reads as 'no bound in force'");
        Assert.Equal(0, host.Kernel.Audit.PendingRecords);
    }

    [Fact]
    public void A_mission_runs_end_to_end_and_the_generic_actuator_clamps_to_its_hardware_limit()
    {
        var host = MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(),
            Manifest = Greenhouse("mm-host-01"),
            StateDirectory = _dir
        });

        host.Major.AcceptCharter(Charter("mm-host-01"), Now);
        var report = host.ExecuteMission(Watering("mm-host-01"), Now);

        Assert.NotNull(report);
        var actuation = host.Major.Actions.First(r => r.Capability == "act.water_valve");
        // The charter allows 25s, but the driver's own hardware bound is 10s — the innermost tier wins.
        Assert.Equal(10, actuation.Parameters["on_s"]);
    }

    [Fact]
    public void A_manifest_with_no_safe_state_fails_bring_up_closed()
    {
        Assert.Throws<HostStartupException>(() => MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(),
            Manifest = new MoundManifest { MoundId = "x", IssuedAt = Now.ToWire(), SafeState = "" },
            StateDirectory = _dir
        }));
    }

    [Fact]
    public void An_unresolvable_driver_fails_bring_up_closed()
    {
        var manifest = Greenhouse("mm-host-01");
        manifest.Hardware["x"] = new HardwareBinding
        {
            Driver = "no_such_driver",
            Settings = new Dictionary<string, string> { ["capability"] = "act.z" }
        };

        Assert.Throws<HostStartupException>(() => MoundHost.Create(new HostOptions
        {
            Keys = Ed25519KeyPair.Generate(),
            Manifest = manifest,
            StateDirectory = _dir
        }));
    }

    [Fact]
    public void Device_identity_is_generated_once_and_reloaded_across_a_restart()
    {
        var first = MoundHost.LoadOrCreateIdentity(_dir);
        var second = MoundHost.LoadOrCreateIdentity(_dir);
        Assert.Equal(first.PublicKey, second.PublicKey);
    }

    [Fact]
    public void A_mid_actuation_restart_over_the_file_store_recovers_without_replay()
    {
        var keys = Ed25519KeyPair.Generate();

        // First life: chartered, then a mission caught mid-actuation, its checkpoint on disk.
        var first = MoundHost.Create(new HostOptions { Keys = keys, Manifest = Greenhouse("mm-r-01"), StateDirectory = _dir });
        first.Major.AcceptCharter(Charter("mm-r-01"), Now);
        first.Cache.SaveAuthority(first.Authority);                       // the charter must survive the restart
        var checkpoint = MissionCheckpoint.Of(Watering("mm-r-01"), Now);
        checkpoint.ActuationInFlight = "water";
        first.Cache.Save(MissionCheckpoint.Key, checkpoint);

        // Reborn over the same directory, linked to a controller so the recovery report can be read.
        var controller = new SimController();
        var controllerKey = controller.Enroll("mm-r-01", keys.PublicKey);
        var controllerKeys = new InMemoryPublicKeyDirectory();
        controllerKeys.Register(KeyIds.Controller, controllerKey);

        var reborn = MoundHost.Create(new HostOptions
        {
            Keys = keys,
            Manifest = Greenhouse("mm-r-01"),
            StateDirectory = _dir,
            Transport = new SimLink(controller),
            ControllerKeys = controllerKeys
        });
        reborn.Restore(Now.AddSeconds(30));
        reborn.Sync(Now.AddSeconds(35));   // drains the recovery report up to the controller

        var report = controller.Account("mm-r-01").Reports.Last(r => r.MissionId == "ms-host");
        Assert.Equal(MissionStates.Failed, report.State);
        Assert.Contains("mid-actuation", report.Detail);   // ambiguous, never replayed
        Assert.False(new FileStateStore(Path.Combine(_dir, "state")).TryGet("cache:" + MissionCheckpoint.Key, out _));
    }
    // ---- P0.4 / P0.5: a restart does not reset what the mound owes (v0.9.33) -------------------

    /// <remarks>
    /// `ActuationHistory` was two in-memory dictionaries that nothing wrote anywhere, so `min_off_s`
    /// and `max_rate_per_h` — limits the manifest declares and the kernel enforces — began empty on
    /// every boot. The 300 s cooldown here was enforced before a restart and gone three seconds
    /// after one, which made rebooting a way to actuate as often as you liked.
    /// </remarks>
    [Fact]
    public void A_cooldown_survives_a_restart()
    {
        var keys = Ed25519KeyPair.Generate();
        var host = MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Greenhouse("mm-h20"), StateDirectory = _dir
        });
        host.Major.AcceptCharter(Charter("mm-h20"), Now);
        host.Cache.SaveAuthority(host.Authority);

        host.ExecuteMission(Watering("mm-h20"), Now);
        var ran = host.Major.Actions.Single(a => a.Capability == "act.water_valve");
        Assert.NotEqual(ActionOutcomes.Refused, ran.Outcome);   // it actuated; the duty cycle is now owed

        // A reboot three seconds later — well inside the 300 s min_off_s.
        var reborn = MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Greenhouse("mm-h20"), StateDirectory = _dir
        });
        reborn.Restore(Now.AddSeconds(3));
        reborn.ExecuteMission(Watering("mm-h20"), Now.AddSeconds(3));

        Assert.Contains(reborn.Major.Actions,
            a => a.Capability == "act.water_valve" && a.Outcome == ActionOutcomes.Refused
                 && a.Detail.Contains("min_off_s"));
    }

    /// <remarks>
    /// The replay hole. The handled-downlink set was in-memory only, so a mound's memory of what it
    /// had already done evaporated on restart — and a controller redelivering a COMPLETED mission
    /// after a reboot got it executed a second time, with the valve opening again. Every other
    /// durable protection was in place; this one was simply never written down.
    /// </remarks>
    [Fact]
    public void A_completed_mission_redelivered_after_a_restart_does_not_run_again()
    {
        var keys = Ed25519KeyPair.Generate();
        var controller = new SimController();
        var link = new SimLink(controller);
        var keyDirectory = new InMemoryPublicKeyDirectory();
        controller.Enroll("mm-h21", keys.PublicKey);
        keyDirectory.Register(KeyIds.Controller, controller.PublicKey);

        var host = MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Greenhouse("mm-h21"), StateDirectory = _dir,
            Transport = link, ControllerKeys = keyDirectory
        });
        controller.IssueCharter(Charter("mm-h21"), Now);
        host.Sync(Now);
        Assert.Equal("chartered", host.State);

        var mission = Watering("mm-h21");
        controller.AssignMission(mission, Now);
        host.Sync(Now.AddSeconds(1));
        Assert.True(host.Major.Actions.Any(a => a.Capability == "act.water_valve"),
            "the downlinked mission should have actuated once");

        // The mound reboots, and the controller redelivers the very same signed envelope — as far as
        // it knows, its ack was lost.
        var reborn = MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Greenhouse("mm-h21"), StateDirectory = _dir,
            Transport = link, ControllerKeys = keyDirectory
        });
        reborn.Restore(Now.AddSeconds(2));
        controller.AssignMission(mission, Now.AddSeconds(2));

        reborn.Sync(Now.AddSeconds(3));

        Assert.False(reborn.Major.Actions.Any(a => a.Capability == "act.water_valve"),
            "the redelivered mission ran again after the restart");
    }

    /// <remarks>
    /// The bounded ledger's edge, and why the bound is a horizon rather than a count: an id that
    /// ages out becomes executable again, which is replay by expiry. So an envelope older than the
    /// horizon is refused rather than run — the mound can no longer prove it has not already done it.
    /// </remarks>
    [Fact]
    public void An_envelope_older_than_the_replay_horizon_is_refused_not_run()
    {
        var keys = Ed25519KeyPair.Generate();
        var controller = new SimController();
        var link = new SimLink(controller);
        var keyDirectory = new InMemoryPublicKeyDirectory();
        controller.Enroll("mm-h22", keys.PublicKey);
        keyDirectory.Register(KeyIds.Controller, controller.PublicKey);

        var host = MoundHost.Create(new HostOptions
        {
            Keys = keys, Manifest = Greenhouse("mm-h22"), StateDirectory = _dir,
            Transport = link, ControllerKeys = keyDirectory
        });
        controller.IssueCharter(Charter("mm-h22"), Now);
        host.Sync(Now);

        // A mission signed now, delivered to a mound whose clock is well past the horizon.
        var mission = Watering("mm-h22");
        mission.ExpiresAt = Now.AddDays(30).ToWire();
        controller.AssignMission(mission, Now);

        host.Sync(Now + RunnerAnt.ReplayHorizon + TimeSpan.FromHours(1));

        Assert.False(host.Major.Actions.Any(a => a.Capability == "act.water_valve"),
            "an envelope past the replay horizon was executed");
        Assert.Contains(host.Runner.Audit, line => line.Contains("replay horizon"));
    }

}
