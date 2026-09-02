using Micromound.Capabilities;
using Micromound.Drivers;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The M4 driver-resolution seam: a manifest's hardware bindings turned into configured generic
/// driver primitives, fail-closed as a whole. These prove the composition a real host will run
/// between "a manifest arrived" and "the kernel has executors to bind", plus the two primitives
/// (a generic digital actuator and a generic analog sensor) that specialize by capability, not by
/// being a device-specific driver type.
/// </summary>
public class DriverCompositionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    private static DriverFactoryRegistry Factories()
    {
        var factories = new DriverFactoryRegistry();
        factories.Register(new AnalogSensorFactory());
        factories.Register(new DigitalActuatorFactory());
        return factories;
    }

    private static MoundManifest Manifest(params (string device, string driver, Dictionary<string, string> settings)[] devices)
    {
        var manifest = new MoundManifest();
        foreach (var (device, driver, settings) in devices)
            manifest.Hardware[device] = new HardwareBinding { Driver = driver, Settings = settings };
        return manifest;
    }

    private static Dictionary<string, string> Soil() =>
        new() { ["capability"] = "sense.soil_moisture", ["unit"] = "pct" };

    private static Dictionary<string, string> Valve() =>
        new() { ["capability"] = "act.water_valve", ["max_on_s"] = "10", ["min_off_s"] = "300" };

    [Fact]
    public void A_greenhouse_manifest_resolves_to_two_distinctly_identified_generic_drivers()
    {
        var resolution = ManifestDriverComposer.Compose(
            Manifest(("soil", "analog_sensor", Soil()), ("irrigation", "digital_actuator", Valve())),
            Factories());

        Assert.True(resolution.IsValid);
        Assert.Equal(2, resolution.Drivers.Count);
        Assert.Contains(resolution.Drivers, d => d.DriverId == "analog_sensor:sense.soil_moisture");
        Assert.Contains(resolution.Drivers, d => d.DriverId == "digital_actuator:act.water_valve");
    }

    [Fact]
    public void A_resolved_actuator_carries_its_hardware_limits_from_the_manifest()
    {
        var resolution = ManifestDriverComposer.Compose(
            Manifest(("irrigation", "digital_actuator", Valve())), Factories());

        var descriptor = Assert.Single(resolution.Drivers[0].Capabilities);
        Assert.Equal("act.water_valve", descriptor.Id);
        Assert.Equal(ActionClass.Benign, descriptor.Class);
        Assert.Equal(10, descriptor.HardwareLimits.MaxOnSeconds);
        Assert.Contains("on_s", descriptor.Parameters);
    }

    [Fact]
    public void A_resolved_sensor_is_an_observe_capability()
    {
        var resolution = ManifestDriverComposer.Compose(
            Manifest(("soil", "analog_sensor", Soil())), Factories());

        var descriptor = Assert.Single(resolution.Drivers[0].Capabilities);
        Assert.Equal(ActionClass.Observe, descriptor.Class);
    }

    [Fact]
    public void An_unknown_driver_type_fails_closed()
    {
        var resolution = ManifestDriverComposer.Compose(
            Manifest(("x", "no_such_driver", new Dictionary<string, string> { ["capability"] = "act.x" })),
            Factories());

        Assert.False(resolution.IsValid);
        Assert.Empty(resolution.Drivers);
    }

    [Fact]
    public void A_capability_of_the_wrong_kind_for_its_driver_fails_closed()
    {
        var resolution = ManifestDriverComposer.Compose(
            Manifest(("x", "digital_actuator", new Dictionary<string, string> { ["capability"] = "sense.wrong" })),
            Factories());

        Assert.False(resolution.IsValid);
    }

    [Fact]
    public void A_missing_required_setting_fails_closed()
    {
        var resolution = ManifestDriverComposer.Compose(
            Manifest(("x", "analog_sensor", new Dictionary<string, string>())), Factories());

        Assert.False(resolution.IsValid);
    }

    [Fact]
    public void A_non_numeric_limit_fails_closed()
    {
        var resolution = ManifestDriverComposer.Compose(
            Manifest(("x", "digital_actuator",
                new Dictionary<string, string> { ["capability"] = "act.v", ["max_on_s"] = "soon" })),
            Factories());

        Assert.False(resolution.IsValid);
    }

    [Fact]
    public void Two_devices_that_resolve_to_the_same_driver_identity_fail_closed()
    {
        var resolution = ManifestDriverComposer.Compose(
            Manifest(
                ("a", "digital_actuator", new Dictionary<string, string> { ["capability"] = "act.v" }),
                ("b", "digital_actuator", new Dictionary<string, string> { ["capability"] = "act.v" })),
            Factories());

        Assert.False(resolution.IsValid);
        Assert.Empty(resolution.Drivers);
    }

    [Fact]
    public void One_bad_device_discards_the_whole_resolution_never_half_wired()
    {
        var resolution = ManifestDriverComposer.Compose(
            Manifest(("good", "analog_sensor", Soil()), ("bad", "no_such", new Dictionary<string, string>())),
            Factories());

        Assert.False(resolution.IsValid);
        Assert.Empty(resolution.Drivers);   // the good device is discarded too — fail closed as a whole
    }

    [Fact]
    public void The_actuator_holds_the_line_for_its_duration_then_releases_and_produces_no_evidence()
    {
        var line = new InMemoryDigitalOutput();
        var driver = new DigitalActuatorDriver(line);
        driver.Configure(new Dictionary<string, string> { ["capability"] = "act.water_valve", ["max_on_s"] = "10" });

        var outcome = driver.Executors[0].Execute(new CapabilityExecution
        {
            CapabilityId = "act.water_valve",
            Parameters = new Dictionary<string, double> { ["on_s"] = 8 },
            StartedAt = Now,
            EffectiveLimits = new CapabilityLimits { MaxOnSeconds = 10 }
        });

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, driver.Actuations);
        Assert.Equal(8, driver.LastOnSeconds);
        Assert.True(line.State);                  // HELD active for its duration — a real valve is open
        Assert.True(driver.IsHolding);
        Assert.Empty(outcome.Evidence);           // a command is not evidence

        driver.ServiceHolds(Now.AddSeconds(5));   // before the deadline: still held
        Assert.True(line.State);

        driver.ServiceHolds(Now.AddSeconds(8));   // deadline reached: released
        Assert.False(line.State);
        Assert.False(driver.IsHolding);
    }

    [Fact]
    public void An_actuation_with_no_duration_faults_rather_than_latching()
    {
        var driver = new DigitalActuatorDriver(new InMemoryDigitalOutput());
        driver.Configure(new Dictionary<string, string> { ["capability"] = "act.water_valve" });

        var outcome = driver.Executors[0].Execute(new CapabilityExecution
        {
            CapabilityId = "act.water_valve",
            Parameters = new Dictionary<string, double>(),   // no on_s
            StartedAt = Now,
            EffectiveLimits = new CapabilityLimits()
        });

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void The_actuator_safe_state_drives_the_line_inactive()
    {
        var line = new InMemoryDigitalOutput();
        var driver = new DigitalActuatorDriver(line);
        driver.Configure(new Dictionary<string, string> { ["capability"] = "act.water_valve" });

        line.Write(true);                        // pretend the line is energized
        driver.EnterSafeState();
        Assert.False(line.State);                // de-energized
    }

    [Theory]
    [InlineData("NaN")]        // Math.Min(NaN, x) = NaN would neutralize every narrower limit tier
    [InlineData("Infinity")]
    [InlineData("-5")]         // a negative limit is nonsense, not a bound
    [InlineData("soon")]       // not a number
    public void A_bad_hardware_limit_fails_closed(string badLimit)
    {
        var resolution = ManifestDriverComposer.Compose(
            Manifest(("x", "digital_actuator",
                new Dictionary<string, string> { ["capability"] = "act.v", ["max_on_s"] = badLimit })),
            Factories());

        Assert.False(resolution.IsValid);
        Assert.Empty(resolution.Drivers);
    }

    [Fact]
    public void A_malformed_capability_id_fails_closed()
    {
        // Only a prefix check would pass "act." — but the registry demands a well-formed id, so the
        // composer must too, or the driver resolves yet its capability never registers (half-wired).
        var resolution = ManifestDriverComposer.Compose(
            Manifest(("x", "digital_actuator",
                new Dictionary<string, string> { ["capability"] = "act.", ["max_on_s"] = "10" })),
            Factories());

        Assert.False(resolution.IsValid);
    }

    [Fact]
    public void An_unparseable_active_high_fails_closed_so_safe_state_polarity_is_known()
    {
        var resolution = ManifestDriverComposer.Compose(
            Manifest(("x", "digital_actuator",
                new Dictionary<string, string> { ["capability"] = "act.v", ["active_high"] = "yes" })),
            Factories());

        Assert.False(resolution.IsValid);
    }

    [Fact]
    public void An_active_low_actuator_de_energizes_to_low_on_safe_state()
    {
        var line = new InMemoryDigitalOutput();
        var driver = new DigitalActuatorDriver(line);
        driver.Configure(new Dictionary<string, string> { ["capability"] = "act.v", ["active_high"] = "false" });

        // For an active-low actuator the safe (inactive) level is HIGH.
        driver.EnterSafeState();
        Assert.True(line.State);
    }

    [Fact]
    public void A_failed_reconfigure_leaves_nothing_exposed()
    {
        var driver = new AnalogSensorDriver(new InMemoryAnalogInput());
        driver.Configure(new Dictionary<string, string> { ["capability"] = "sense.soil_moisture" });
        Assert.Single(driver.Capabilities);      // configured once, exposing its capability

        var reconfigured = driver.Configure(new Dictionary<string, string>());   // now bad
        Assert.False(reconfigured.IsValid);
        Assert.Equal(DriverHealth.Absent, driver.Health);
        Assert.Empty(driver.Capabilities);       // the stale capability is gone, not still exposed
        Assert.Empty(driver.Executors);
    }

    [Fact]
    public void The_sensor_executor_reads_the_channel_and_emits_it_as_evidence()
    {
        var channel = new InMemoryAnalogInput { Value = 17.5 };
        var driver = new AnalogSensorDriver(channel);
        EvidenceItem? published = null;
        driver.Publish = item => published = item;
        driver.Configure(new Dictionary<string, string> { ["capability"] = "sense.soil_moisture", ["unit"] = "pct" });

        var outcome = driver.Executors[0].Execute(new CapabilityExecution
        {
            CapabilityId = "sense.soil_moisture",
            Parameters = new Dictionary<string, double>(),
            StartedAt = Now,
            EffectiveLimits = new CapabilityLimits()
        });

        Assert.True(outcome.Succeeded);
        var evidence = Assert.Single(outcome.Evidence);
        Assert.NotNull(published);
        Assert.True(EvidenceReadings.TryRead(evidence, out var value));
        Assert.Equal(17.5, value);
    }

    [Fact]
    public void A_driver_that_cannot_configure_is_absent()
    {
        var driver = new AnalogSensorDriver(new InMemoryAnalogInput());
        var result = driver.Configure(new Dictionary<string, string>());   // no capability

        Assert.False(result.IsValid);
        Assert.Equal(DriverHealth.Absent, driver.Health);
        Assert.Empty(driver.Capabilities);   // nothing is exposed by an unconfigured driver
    }
}
