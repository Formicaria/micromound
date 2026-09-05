using Micromound.Drivers;
using Micromound.Host;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The operator's question at the board — "does my wiring match my manifest?" — answered by
/// <see cref="HardwareCheck"/> without composing a mound or actuating anything. These run the check
/// over fake ports: a fake GPIO kernel and a fake ADS1115, the same fakes the driver tests use, so what
/// is proven is the check's own behaviour (claim, one read, report, safe level, never actuate).
/// </summary>
public sealed class HardwareCheckTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-05T12:00:00Z");

    /// <summary>A GPIO kernel that hands out line descriptors and records every value set.</summary>
    private sealed class FakeKernel : ILinuxIo
    {
        public List<(ulong Bits, ulong Mask)> Sets { get; } = [];
        public bool ChipMissing { get; set; }
        public ulong? RequestedInitial { get; private set; }
        private int _fd = 20;
        public int Open(string path, int flags) => ChipMissing ? -1 : _fd++;
        public int Ioctl(int fd, uint request, byte[] buffer)
        {
            if (request == GpioChardevOutput.GetLineIoctl)
            {
                RequestedInitial = GpioChardevOutput.DecodeLineRequest(buffer).AttrValues;
                GpioChardevOutput.WriteRequestedFd(buffer, _fd++);
                return 0;
            }
            if (request == GpioChardevOutput.SetValuesIoctl) { Sets.Add(GpioChardevOutput.DecodeLineValues(buffer)); return 0; }
            return -1;
        }
        public int Close(int fd) => 0;
        public int LastErrno() => 2;
    }

    /// <summary>An ADS1115 that answers idle and converts to a fixed raw value — or is not there.</summary>
    private sealed class FakeChip(short raw) : II2cBus
    {
        public bool Present { get; set; } = true;
        private byte _pointer;
        public void Write(ReadOnlySpan<byte> data) { if (!Present) throw new IOException("no acknowledge (errno 121)"); _pointer = data[0]; }
        public void Read(Span<byte> buffer)
        {
            if (!Present) throw new IOException("no acknowledge (errno 121)");
            ushort v = _pointer == 0x00 ? (ushort)raw : (ushort)0x8583;   // conversion / config (idle → OS set)
            buffer[0] = (byte)(v >> 8); buffer[1] = (byte)(v & 0xFF);
        }
    }

    private static MoundManifest Manifest(params (string Device, string Driver, Dictionary<string, string> Settings)[] bindings)
    {
        var m = new MoundManifest { ManifestId = "mf", MoundId = "mm-check", IssuedAt = Now.ToWire(), SafeState = "all_actuators_off" };
        foreach (var (device, driver, settings) in bindings)
            m.Hardware[device] = new HardwareBinding { Driver = driver, Settings = settings };
        return m;
    }

    private static DriverFactoryRegistry Registry(FakeKernel kernel, FakeChip chip)
    {
        var r = new DriverFactoryRegistry();
        r.Register(new GpioChardevActuatorFactory(io: kernel));
        r.Register(new Ads1115AnalogSensorFactory(busFactory: (_, _) => chip));
        return r;
    }

    [Fact]
    public void A_good_manifest_claims_every_port_reads_each_sensor_once_and_actuates_nothing()
    {
        var kernel = new FakeKernel();
        var chip = new FakeChip(16384);   // half scale → 2.048 V at ±4.096
        var manifest = Manifest(
            ("valve", "digital_actuator", new() { ["capability"] = "act.water_valve", ["pin"] = "17", ["active_high"] = "false", ["max_on_s"] = "10" }),
            ("soil", "analog_sensor", new() { ["capability"] = "sense.soil_moisture", ["channel"] = "0", ["unit"] = "pct", ["scale"] = "50" }));

        var report = HardwareCheck.Run(manifest, Registry(kernel, chip), Now);

        Assert.True(report.AllOk, HardwareCheck.Format(report, "test"));
        var valve = report.Devices.Single(d => d.Device == "valve");
        var soil = report.Devices.Single(d => d.Device == "soil");
        Assert.Equal("act.water_valve", valve.Capability);
        Assert.Contains("SAFE level", valve.Detail);
        Assert.Null(valve.Reading);
        Assert.Equal(1UL, kernel.RequestedInitial);            // active-low: requested HIGH = de-energized
        Assert.All(kernel.Sets, s => Assert.Equal(1UL, s.Bits)); // and never driven active
        Assert.Equal(2.048 * 50, soil.Reading!.Value, 9);       // volts × scale, in the manifest's unit
        Assert.Equal("pct", soil.Unit);
        Assert.Contains("first reading 102.4 pct", soil.Detail);
    }

    [Fact]
    public void A_missing_chip_and_a_missing_gpio_chip_are_reported_per_device_with_the_drivers_reason()
    {
        var kernel = new FakeKernel { ChipMissing = true };
        var chip = new FakeChip(0) { Present = false };
        var manifest = Manifest(
            ("valve", "digital_actuator", new() { ["capability"] = "act.v", ["pin"] = "17" }),
            ("soil", "analog_sensor", new() { ["capability"] = "sense.s", ["channel"] = "0" }));

        var report = HardwareCheck.Run(manifest, Registry(kernel, chip), Now);

        Assert.False(report.AllOk);
        Assert.All(report.Devices, d => Assert.False(d.Ok));
        Assert.Contains("gpiochip0", report.Devices.Single(d => d.Device == "valve").Detail);
        Assert.Contains("no acknowledge", report.Devices.Single(d => d.Device == "soil").Detail);
        Assert.Contains("2 of 2 device(s) refused", HardwareCheck.Format(report, "test"));
    }

    [Fact]
    public void A_chip_that_answers_the_probe_but_not_the_read_is_a_distinct_failure()
    {
        var manifest = Manifest(("soil", "analog_sensor", new() { ["capability"] = "sense.s", ["channel"] = "1" }));

        // Probe succeeds at configure; the check's read then fails.
        var factoryProbeThenFail = new DriverFactoryRegistry();
        factoryProbeThenFail.Register(new Ads1115AnalogSensorFactory(busFactory: (_, _) => new FailAfterProbe()));

        var report = HardwareCheck.Run(manifest, factoryProbeThenFail, Now);

        var soil = Assert.Single(report.Devices);
        Assert.False(soil.Ok);
        Assert.Contains("claimed, but the first read failed", soil.Detail);
    }

    /// <summary>Answers the constructor's probe, then stops acknowledging.</summary>
    private sealed class FailAfterProbe : II2cBus
    {
        private int _reads;
        public void Write(ReadOnlySpan<byte> data) { }
        public void Read(Span<byte> buffer)
        {
            if (_reads++ > 0) throw new IOException("bus error (errno 5)");
            buffer[0] = 0x85; buffer[1] = 0x83;
        }
    }

    [Fact]
    public void An_unknown_driver_type_names_what_this_build_has()
    {
        var manifest = Manifest(("temp", "bme280", new() { ["address"] = "0x76" }));

        var report = HardwareCheck.Run(manifest, Registry(new FakeKernel(), new FakeChip(0)), Now);

        var temp = Assert.Single(report.Devices);
        Assert.False(temp.Ok);
        Assert.Contains("no driver 'bme280'", temp.Detail);
        Assert.Contains("analog_sensor, digital_actuator", temp.Detail);
    }

    [Fact]
    public void A_manifest_with_no_hardware_passes_and_says_so()
    {
        var report = HardwareCheck.Run(Manifest(), Registry(new FakeKernel(), new FakeChip(0)), Now);
        Assert.True(report.AllOk);
        Assert.Contains("binds no hardware", HardwareCheck.Format(report, "test"));
    }

    [Fact]
    public void Devices_naming_physical_ports_are_the_ones_an_in_memory_run_would_fake()
    {
        var manifest = Manifest(
            ("valve", "digital_actuator", new() { ["capability"] = "act.v", ["pin"] = "17" }),
            ("soil", "analog_sensor", new() { ["capability"] = "sense.s", ["channel"] = "0" }),
            ("virtual", "analog_sensor", new() { ["capability"] = "sense.v", ["unit"] = "pct" }));

        Assert.Equal(["valve", "soil"], HardwareCheck.DevicesNamingPhysicalPorts(manifest));
    }
}
