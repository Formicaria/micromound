using Micromound.Capabilities;
using Micromound.Drivers;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The first real hardware backing for a digital line: <see cref="SysfsDigitalOutput"/> over the
/// Linux <c>/sys/class/gpio</c> file protocol, and the <see cref="SysfsDigitalActuatorFactory"/> that
/// reads a line's <c>pin</c> from the manifest and gives the generic actuator a real port instead of
/// an in-memory one. These run against a FAKE sysfs tree (a temp directory) so the file protocol and
/// the pin-parsing / fail-closed wiring are exercised with no hardware; the value writes themselves
/// are what must still be verified on a real board.
/// </summary>
public sealed class SysfsGpioTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");

    private readonly string _root;

    public SysfsGpioTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mm-sysfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Simulate the kernel having created a pin's directory (what <c>export</c> triggers).</summary>
    private string ExportPin(int pin)
    {
        var dir = Path.Combine(_root, "gpio" + pin);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Constructing_a_port_declares_the_pin_an_output_already_at_its_initial_level()
    {
        // "low"/"high" set direction AND value in one write (v0.9.16), so the pin never sits at the
        // wrong level between becoming an output and being driven safe. The default initial level is low.
        var pinDir = ExportPin(17);
        using var port = new SysfsDigitalOutput(17, _root);
        Assert.Equal("low", File.ReadAllText(Path.Combine(pinDir, "direction")));
        Assert.False(port.State);

        var highDir = ExportPin(18);
        using var high = new SysfsDigitalOutput(18, _root, initialHigh: true);
        Assert.Equal("high", File.ReadAllText(Path.Combine(highDir, "direction")));
        Assert.True(high.State);
    }

    [Fact]
    public void Writing_high_then_low_lands_1_then_0_in_the_value_file()
    {
        var pinDir = ExportPin(17);
        using var port = new SysfsDigitalOutput(17, _root);

        port.Write(true);
        Assert.Equal("1", File.ReadAllText(Path.Combine(pinDir, "value")));
        Assert.True(port.State);

        port.Write(false);
        Assert.Equal("0", File.ReadAllText(Path.Combine(pinDir, "value")));
        Assert.False(port.State);
    }

    [Fact]
    public void An_unexported_pin_is_claimed_via_the_export_file()
    {
        // The pin directory does not exist yet, so the port must write the pin number to export to
        // claim it. On a real board the kernel then creates the directory; our fake tree does not, so
        // the subsequent direction write throws — which is exactly the fail-closed signal a driver
        // turns into a refusal. We assert the export attempt happened before that.
        Assert.ThrowsAny<IOException>(() => new SysfsDigitalOutput(23, _root));
        Assert.Equal("23", File.ReadAllText(Path.Combine(_root, "export")));
    }

    [Fact]
    public void Disposing_releases_the_pin_via_the_unexport_file()
    {
        ExportPin(17);
        var port = new SysfsDigitalOutput(17, _root);
        port.Dispose();

        Assert.Equal("17", File.ReadAllText(Path.Combine(_root, "unexport")));
    }

    [Fact]
    public void Dispose_is_idempotent_and_never_double_releases()
    {
        ExportPin(17);
        var port = new SysfsDigitalOutput(17, _root);
        port.Dispose();
        File.Delete(Path.Combine(_root, "unexport"));   // prove a second Dispose writes nothing
        port.Dispose();

        Assert.False(File.Exists(Path.Combine(_root, "unexport")));
    }

    [Fact]
    public void An_already_exported_pin_is_reused_not_re_exported()
    {
        // A prior run that did not release the pin (its directory already exists) is not an error.
        ExportPin(17);
        using var port = new SysfsDigitalOutput(17, _root);

        Assert.False(File.Exists(Path.Combine(_root, "export")));   // reuse: export was not touched
    }

    [Fact]
    public void A_negative_pin_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SysfsDigitalOutput(-1, _root));
    }

    // --- The factory wiring: the generic actuator over a real GPIO port keyed by the manifest pin ---

    [Fact]
    public void The_sysfs_factory_configures_a_generic_actuator_over_the_pin()
    {
        var pinDir = ExportPin(17);
        var driver = new SysfsDigitalActuatorFactory(_root).Create();

        var result = driver.Configure(new Dictionary<string, string>
        {
            ["capability"] = "act.water_valve",
            ["pin"] = "17",
            ["max_on_s"] = "10"
        });

        Assert.True(result.IsValid);
        Assert.Equal("digital_actuator:act.water_valve", driver.DriverId);
        // Configuring drove the line to its safe (inactive, low) level.
        Assert.Equal("0", File.ReadAllText(Path.Combine(pinDir, "value")));
    }

    [Fact]
    public void The_sysfs_actuator_holds_the_real_value_file_high_then_a_sweep_releases_it()
    {
        var pinDir = ExportPin(17);
        var driver = new SysfsDigitalActuatorFactory(_root).Create();
        driver.Configure(new Dictionary<string, string>
        {
            ["capability"] = "act.water_valve",
            ["pin"] = "17"
        });

        var outcome = driver.Executors[0].Execute(new CapabilityExecution
        {
            CapabilityId = "act.water_valve",
            Parameters = new Dictionary<string, double> { ["on_s"] = 5 },
            StartedAt = Now,
            EffectiveLimits = new CapabilityLimits { MaxOnSeconds = 10 }
        });

        Assert.True(outcome.Succeeded);
        // Held active for its duration — a real valve is actually open, not pulsed for a microsecond.
        Assert.Equal("1", File.ReadAllText(Path.Combine(pinDir, "value")));

        ((ITimedDriver)driver).ServiceHolds(Now.AddSeconds(5));   // the clock-driven deadline sweep
        Assert.Equal("0", File.ReadAllText(Path.Combine(pinDir, "value")));   // released to safe
    }

    [Fact]
    public void A_missing_pin_setting_fails_closed()
    {
        var driver = new SysfsDigitalActuatorFactory(_root).Create();

        var result = driver.Configure(new Dictionary<string, string> { ["capability"] = "act.v" });

        Assert.False(result.IsValid);
        Assert.Equal(DriverHealth.Absent, driver.Health);
        Assert.Empty(driver.Capabilities);   // nothing exposed over a port that could not be opened
    }

    [Fact]
    public void A_non_integer_pin_fails_closed()
    {
        var driver = new SysfsDigitalActuatorFactory(_root).Create();

        var result = driver.Configure(new Dictionary<string, string>
        {
            ["capability"] = "act.v",
            ["pin"] = "GPIO17"
        });

        Assert.False(result.IsValid);
        Assert.Equal(DriverHealth.Absent, driver.Health);
    }

    [Fact]
    public void A_pin_whose_line_cannot_be_opened_fails_closed_not_half_wired()
    {
        // No pre-created directory: the export cannot settle in the fake tree, so opening the port
        // throws — the driver must catch that and stay Absent, exposing no capability or executor.
        var driver = new SysfsDigitalActuatorFactory(_root).Create();

        var result = driver.Configure(new Dictionary<string, string>
        {
            ["capability"] = "act.v",
            ["pin"] = "99"
        });

        Assert.False(result.IsValid);
        Assert.Equal(DriverHealth.Absent, driver.Health);
        Assert.Empty(driver.Executors);
    }

    // --- A real port's writes can THROW; the momentary pulse must stay fail-safe ---

    /// <summary>A port whose value writes throw on demand — the failure mode an in-memory line never
    /// had, and the one that could latch a physical line hot.</summary>
    private sealed class ThrowingDigitalOutput : IDigitalOutput
    {
        private readonly int _throwOnWriteNumber;
        private int _writes;
        public bool State { get; private set; }
        public int SafeWrites { get; private set; }
        public ThrowingDigitalOutput(int throwOnWriteNumber) => _throwOnWriteNumber = throwOnWriteNumber;

        public void Write(bool high)
        {
            _writes++;
            if (_writes == _throwOnWriteNumber)
                throw new IOException("simulated GPIO write failure");
            State = high;
            if (!high) SafeWrites++;   // count reaching the safe (low) level
        }
    }

    [Fact]
    public void A_failure_to_energize_faults_and_leaves_nothing_actuated()
    {
        // Writes: 1 = configure's initial safe write, 2 = energize (throws here).
        var line = new ThrowingDigitalOutput(throwOnWriteNumber: 2);
        var driver = new DigitalActuatorDriver(line);
        driver.Configure(new Dictionary<string, string> { ["capability"] = "act.water_valve" });

        var outcome = driver.Executors[0].Execute(new CapabilityExecution
        {
            CapabilityId = "act.water_valve",
            Parameters = new Dictionary<string, double> { ["on_s"] = 5 },
            StartedAt = Now,
            EffectiveLimits = new CapabilityLimits()
        });

        Assert.False(outcome.Succeeded);
        Assert.False(line.State);   // never energized
    }
}
