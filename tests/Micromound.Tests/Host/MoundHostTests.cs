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
}
