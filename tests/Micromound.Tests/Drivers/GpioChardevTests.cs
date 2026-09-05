using Micromound.Capabilities;
using Micromound.Drivers;
using Micromound.Host;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The GPIO character-device backing (<see cref="GpioChardevOutput"/>, uapi v2) and its factory. The
/// risk in this port is the hand-encoded <c>gpio_v2_line_request</c>: a wrong offset and the kernel
/// requests the wrong line, or as an input, or at the wrong initial level. So the layout constants are
/// pinned to the values the kernel's own <c>linux/gpio.h</c> gives (sizes, field offsets, ioctl numbers —
/// measured with a C compiler against the header), and a fake kernel at the system-call seam decodes
/// every request the driver makes. The ioctls themselves must still be verified on a board.
/// </summary>
public sealed class GpioChardevTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-05T12:00:00Z");

    /// <summary>A fake kernel: records opens, decodes line requests, hands out line descriptors, records value sets.</summary>
    private sealed class FakeKernel : ILinuxIo
    {
        public List<(string Path, int Flags)> Opens { get; } = [];
        public List<int> Closed { get; } = [];
        public List<(int ChipFd, uint Line, string Consumer, ulong Flags, uint NumAttrs, uint AttrId, ulong AttrValues, ulong AttrMask, uint NumLines)> LineRequests { get; } = [];
        public List<(int LineFd, ulong Bits, ulong Mask)> ValueSets { get; } = [];
        public HashSet<string> MissingChips { get; } = [];
        public HashSet<uint> BusyLines { get; } = [];
        public bool FailSetValues { get; set; }
        public int Errno { get; private set; }

        private int _nextFd = 10;
        private readonly HashSet<int> _openChipFds = [];
        private readonly HashSet<int> _lineFds = [];

        public IReadOnlySet<int> OpenLineFds => _lineFds;
        public IReadOnlySet<int> OpenChipFds => _openChipFds;

        public int Open(string path, int flags)
        {
            Opens.Add((path, flags));
            if (MissingChips.Contains(path)) { Errno = 2; return -1; }   // ENOENT
            var fd = _nextFd++;
            _openChipFds.Add(fd);
            return fd;
        }

        public int Ioctl(int fd, uint request, byte[] buffer)
        {
            switch (request)
            {
                case GpioChardevOutput.GetLineIoctl:
                {
                    if (!_openChipFds.Contains(fd)) { Errno = 9; return -1; }   // EBADF
                    if (buffer.Length != GpioChardevOutput.LineRequestSize) { Errno = 22; return -1; }   // EINVAL: the kernel checks the size via the ioctl number
                    var r = GpioChardevOutput.DecodeLineRequest(buffer);
                    LineRequests.Add((fd, r.Line, r.Consumer, r.Flags, r.NumAttrs, r.AttrId, r.AttrValues, r.AttrMask, r.NumLines));
                    if (BusyLines.Contains(r.Line)) { Errno = 16; return -1; }  // EBUSY
                    var lineFd = _nextFd++;
                    _lineFds.Add(lineFd);
                    GpioChardevOutput.WriteRequestedFd(buffer, lineFd);
                    return 0;
                }
                case GpioChardevOutput.SetValuesIoctl:
                {
                    if (!_lineFds.Contains(fd)) { Errno = 9; return -1; }
                    if (FailSetValues) { Errno = 5; return -1; }   // EIO
                    var v = GpioChardevOutput.DecodeLineValues(buffer);
                    ValueSets.Add((fd, v.Bits, v.Mask));
                    return 0;
                }
                default:
                    Errno = 25;   // ENOTTY: not an ioctl we know
                    return -1;
            }
        }

        public int Close(int fd)
        {
            Closed.Add(fd);
            _openChipFds.Remove(fd);
            _lineFds.Remove(fd);
            return 0;
        }

        public int LastErrno() => Errno;
    }

    // ---- the encoding, pinned to linux/gpio.h ----

    [Fact]
    public void Layout_constants_match_the_kernel_header()
    {
        // Measured from <linux/gpio.h> with gcc: sizeof(struct gpio_v2_line_request) == 592,
        // sizeof(struct gpio_v2_line_values) == 16, GPIO_V2_GET_LINE_IOCTL == 0xC250B407,
        // GPIO_V2_LINE_SET_VALUES_IOCTL == 0xC010B40F, GPIO_V2_LINE_FLAG_OUTPUT == 8,
        // GPIO_V2_LINE_ATTR_ID_OUTPUT_VALUES == 2. The ioctl number encodes the struct size
        // (bits 16..29), so a wrong size would be refused by the kernel with EINVAL/ENOTTY.
        Assert.Equal(592, GpioChardevOutput.LineRequestSize);
        Assert.Equal(16, GpioChardevOutput.LineValuesSize);
        Assert.Equal(0xC250B407u, GpioChardevOutput.GetLineIoctl);
        Assert.Equal(0xC010B40Fu, GpioChardevOutput.SetValuesIoctl);
        Assert.Equal((uint)GpioChardevOutput.LineRequestSize, (GpioChardevOutput.GetLineIoctl >> 16) & 0x3FFF);   // size field of _IOWR
        Assert.Equal((uint)GpioChardevOutput.LineValuesSize, (GpioChardevOutput.SetValuesIoctl >> 16) & 0x3FFF);
        Assert.Equal(8UL, GpioChardevOutput.FlagOutput);
        Assert.Equal(2u, GpioChardevOutput.AttrIdOutputValues);
    }

    [Fact]
    public void A_line_request_places_every_field_at_the_header_offset()
    {
        var buffer = GpioChardevOutput.BuildLineRequest(17, initialHigh: true, "micromound");

        // Field offsets from the header: offsets[0]@0, consumer@256, config.flags@288,
        // config.num_attrs@296, config.attrs[0].attr.id@320, .values@328, .mask@336, num_lines@560, fd@588.
        Assert.Equal(17u, BitConverter.ToUInt32(buffer, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(buffer, 4));                      // offsets[1] unused
        Assert.Equal("micromound", System.Text.Encoding.ASCII.GetString(buffer, 256, 10));
        Assert.Equal(0, buffer[266]);                                            // NUL-terminated
        Assert.Equal(8UL, BitConverter.ToUInt64(buffer, 288));                   // OUTPUT
        Assert.Equal(1u, BitConverter.ToUInt32(buffer, 296));                    // one attribute
        Assert.Equal(2u, BitConverter.ToUInt32(buffer, 320));                    // OUTPUT_VALUES
        Assert.Equal(1UL, BitConverter.ToUInt64(buffer, 328));                   // values: bit 0 = high
        Assert.Equal(1UL, BitConverter.ToUInt64(buffer, 336));                   // mask: applies to line 0 of the request
        Assert.Equal(1u, BitConverter.ToUInt32(buffer, 560));                    // num_lines
        Assert.Equal(0, BitConverter.ToInt32(buffer, 588));                      // fd: the kernel fills it
        Assert.All(buffer.Skip(344).Take(560 - 344), b => Assert.Equal(0, b));  // the other nine attrs and padding are zero
    }

    [Fact]
    public void The_initial_level_is_carried_in_the_request_not_written_afterwards()
    {
        var low = GpioChardevOutput.DecodeLineRequest(GpioChardevOutput.BuildLineRequest(5, initialHigh: false, "x"));
        var high = GpioChardevOutput.DecodeLineRequest(GpioChardevOutput.BuildLineRequest(5, initialHigh: true, "x"));

        Assert.Equal(0UL, low.AttrValues);
        Assert.Equal(1UL, high.AttrValues);
        Assert.Equal(1UL, low.AttrMask);
        Assert.Equal(1UL, high.AttrMask);
    }

    [Fact]
    public void A_long_consumer_label_is_truncated_and_still_terminated()
    {
        var buffer = GpioChardevOutput.BuildLineRequest(0, false, new string('m', 40));
        Assert.Equal(31, GpioChardevOutput.DecodeLineRequest(buffer).Consumer.Length);
        Assert.Equal(0, buffer[256 + 31]);
    }

    [Fact]
    public void Line_values_set_bit_0_under_mask_bit_0()
    {
        Assert.Equal((1UL, 1UL), GpioChardevOutput.DecodeLineValues(GpioChardevOutput.BuildLineValues(true)));
        Assert.Equal((0UL, 1UL), GpioChardevOutput.DecodeLineValues(GpioChardevOutput.BuildLineValues(false)));
    }

    // ---- the port over the fake kernel ----

    [Fact]
    public void Constructing_a_port_opens_the_chip_requests_the_line_as_output_and_closes_the_chip()
    {
        var kernel = new FakeKernel();

        using var port = new GpioChardevOutput(17, initialHigh: false, chip: 0, kernel);

        var open = Assert.Single(kernel.Opens);
        Assert.Equal("/dev/gpiochip0", open.Path);
        Assert.Equal(LibcIo.O_RDWR | LibcIo.O_CLOEXEC, open.Flags);
        var req = Assert.Single(kernel.LineRequests);
        Assert.Equal(17u, req.Line);
        Assert.Equal("micromound", req.Consumer);
        Assert.Equal(GpioChardevOutput.FlagOutput, req.Flags);
        Assert.Equal(1u, req.NumLines);
        Assert.Equal(GpioChardevOutput.AttrIdOutputValues, req.AttrId);
        Assert.Equal(0UL, req.AttrValues);
        Assert.Empty(kernel.OpenChipFds);          // the chip descriptor is closed once the line is held
        Assert.Single(kernel.OpenLineFds);         // the line descriptor stays open
        Assert.False(port.State);
    }

    [Fact]
    public void The_chip_number_selects_the_device_node()
    {
        var kernel = new FakeKernel();
        using var port = new GpioChardevOutput(3, false, chip: 4, kernel);
        Assert.Equal("/dev/gpiochip4", kernel.Opens[0].Path);
    }

    [Fact]
    public void Writes_are_set_values_ioctls_on_the_line_descriptor()
    {
        var kernel = new FakeKernel();
        using var port = new GpioChardevOutput(17, false, 0, kernel);
        var lineFd = kernel.OpenLineFds.Single();

        port.Write(true);
        port.Write(false);

        Assert.Equal([(lineFd, 1UL, 1UL), (lineFd, 0UL, 1UL)], kernel.ValueSets);
        Assert.False(port.State);
    }

    [Fact]
    public void Disposing_releases_the_line()
    {
        var kernel = new FakeKernel();
        var port = new GpioChardevOutput(17, false, 0, kernel);
        var lineFd = kernel.OpenLineFds.Single();

        port.Dispose();
        port.Dispose();   // idempotent

        Assert.Empty(kernel.OpenLineFds);
        Assert.Equal(1, kernel.Closed.Count(fd => fd == lineFd));
        Assert.Throws<ObjectDisposedException>(() => port.Write(true));
    }

    [Fact]
    public void A_missing_chip_is_an_io_error_with_the_errno()
    {
        var kernel = new FakeKernel();
        kernel.MissingChips.Add("/dev/gpiochip0");

        var ex = Assert.Throws<IOException>(() => new GpioChardevOutput(17, false, 0, kernel));
        Assert.Contains("/dev/gpiochip0", ex.Message);
        Assert.Contains("errno 2", ex.Message);
        Assert.Empty(kernel.OpenLineFds);
    }

    [Fact]
    public void A_line_another_process_holds_is_refused_and_the_chip_is_closed()
    {
        var kernel = new FakeKernel();
        kernel.BusyLines.Add(17);

        var ex = Assert.Throws<IOException>(() => new GpioChardevOutput(17, false, 0, kernel));
        Assert.Contains("EBUSY", ex.Message);
        Assert.Empty(kernel.OpenChipFds);   // no leaked chip descriptor behind the refusal
        Assert.Empty(kernel.OpenLineFds);
    }

    [Fact]
    public void A_failed_write_throws_so_the_driver_can_fault()
    {
        var kernel = new FakeKernel();
        using var port = new GpioChardevOutput(17, false, 0, kernel);
        kernel.FailSetValues = true;

        Assert.Throws<IOException>(() => port.Write(true));
        Assert.False(port.State);   // the level is not claimed to have changed
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void Negative_line_or_chip_is_refused_before_any_syscall(int line, int chip)
    {
        var kernel = new FakeKernel();
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpioChardevOutput(line, false, chip, kernel));
        Assert.Empty(kernel.Opens);
    }

    // ---- the factory: manifest → line, requested at the SAFE level ----

    [Fact]
    public void The_factory_requests_the_manifest_line_at_the_safe_level_for_an_active_high_load()
    {
        var kernel = new FakeKernel();
        var driver = new GpioChardevActuatorFactory(io: kernel).Create();

        var result = driver.Configure(new Dictionary<string, string> { ["capability"] = "act.water_valve", ["pin"] = "17", ["max_on_s"] = "10" });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var req = Assert.Single(kernel.LineRequests);
        Assert.Equal(17u, req.Line);
        Assert.Equal(0UL, req.AttrValues);                        // active-high: safe = low, requested low
        Assert.Equal("/dev/gpiochip0", kernel.Opens[0].Path);      // default chip
        // The driver's own safe write follows; it is the same level, so the line never changes.
        Assert.All(kernel.ValueSets, v => Assert.Equal(0UL, v.Bits));
    }

    [Fact]
    public void An_active_low_load_is_requested_HIGH_so_it_is_never_energized_at_bring_up()
    {
        // The whole point of carrying the initial level in the request: a relay board that switches on
        // a LOW line must come up HIGH from the very first instant it is an output.
        var kernel = new FakeKernel();
        var driver = new GpioChardevActuatorFactory(io: kernel).Create();

        driver.Configure(new Dictionary<string, string> { ["capability"] = "act.heater", ["pin"] = "22", ["active_high"] = "false", ["chip"] = "4" });

        var req = Assert.Single(kernel.LineRequests);
        Assert.Equal(22u, req.Line);
        Assert.Equal(1UL, req.AttrValues);                        // requested HIGH = de-energized
        Assert.Equal("/dev/gpiochip4", kernel.Opens[0].Path);
        Assert.All(kernel.ValueSets, v => Assert.Equal(1UL, v.Bits));   // and every write since has been HIGH
    }

    [Fact]
    public void An_actuation_drives_the_line_active_and_the_safe_state_releases_it()
    {
        var kernel = new FakeKernel();
        var driver = (DigitalActuatorDriver)new GpioChardevActuatorFactory(io: kernel).Create();
        driver.Configure(new Dictionary<string, string> { ["capability"] = "act.water_valve", ["pin"] = "17", ["active_high"] = "false" });
        kernel.ValueSets.Clear();

        var outcome = driver.Executors[0].Execute(new CapabilityExecution
        {
            CapabilityId = "act.water_valve",
            Parameters = new Dictionary<string, double> { ["on_s"] = 5 },
            StartedAt = Now,
            EffectiveLimits = new CapabilityLimits { MaxOnSeconds = 10 }
        });
        Assert.True(outcome.Succeeded);
        Assert.Equal(0UL, kernel.ValueSets[^1].Bits);   // active-low: energized = LOW
        Assert.True(driver.IsHolding);

        driver.EnterSafeState();
        Assert.Equal(1UL, kernel.ValueSets[^1].Bits);   // released = HIGH
        Assert.False(driver.IsHolding);
    }

    [Theory]
    [InlineData("pin", null)]
    [InlineData("pin", "GPIO17")]
    [InlineData("chip", "zero")]
    public void A_malformed_line_location_fails_closed_before_any_syscall(string key, string? raw)
    {
        var kernel = new FakeKernel();
        var settings = new Dictionary<string, string> { ["capability"] = "act.v", ["pin"] = "17" };
        if (raw is null) settings.Remove(key); else settings[key] = raw;

        var driver = new GpioChardevActuatorFactory(io: kernel).Create();
        var result = driver.Configure(settings);

        Assert.False(result.IsValid);
        Assert.Equal(DriverHealth.Absent, driver.Health);
        Assert.Empty(kernel.Opens);
    }

    [Fact]
    public void A_busy_line_refuses_configuration_and_exposes_nothing()
    {
        var kernel = new FakeKernel();
        kernel.BusyLines.Add(17);
        var driver = new GpioChardevActuatorFactory(io: kernel).Create();

        var result = driver.Configure(new Dictionary<string, string> { ["capability"] = "act.v", ["pin"] = "17" });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("EBUSY"));
        Assert.Empty(driver.Capabilities);
        Assert.Empty(driver.Executors);
    }

    [Fact]
    public void A_reconfigure_releases_the_old_line_before_claiming_the_new_one()
    {
        var kernel = new FakeKernel();
        var driver = new GpioChardevActuatorFactory(io: kernel).Create();
        driver.Configure(new Dictionary<string, string> { ["capability"] = "act.v", ["pin"] = "17" });
        var first = kernel.OpenLineFds.Single();

        driver.Configure(new Dictionary<string, string> { ["capability"] = "act.v", ["pin"] = "18" });

        Assert.Contains(first, kernel.Closed);
        Assert.Single(kernel.OpenLineFds);          // exactly one line held, the new one
        Assert.Equal(18u, kernel.LineRequests[^1].Line);
    }

    // ---- the two GPIO backings agree on the manifest ----

    [Fact]
    public void The_sysfs_backing_now_sets_the_initial_level_atomically_too()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-sysfs-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "gpio17"));
        try
        {
            new SysfsDigitalActuatorFactory(root).Create()
                .Configure(new Dictionary<string, string> { ["capability"] = "act.heater", ["pin"] = "17", ["active_high"] = "false" });
            // "high" = become an output already high; never "out" (low) followed by a write.
            Assert.Equal("high", File.ReadAllText(Path.Combine(root, "gpio17", "direction")));
            Assert.Equal("1", File.ReadAllText(Path.Combine(root, "gpio17", "value")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_sysfs_backing_refuses_a_non_default_chip_rather_than_guessing_the_pin()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-sysfs-chip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "gpio17"));
        try
        {
            var result = new SysfsDigitalActuatorFactory(root).Create()
                .Configure(new Dictionary<string, string> { ["capability"] = "act.v", ["pin"] = "17", ["chip"] = "4" });
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, e => e.Contains("chip"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Both_backings_share_the_one_digital_actuator_schema_which_now_describes_chip()
    {
        Assert.Same(DriverSchemaCatalog.DigitalActuator, new GpioChardevActuatorFactory(io: new FakeKernel()).Schema);
        Assert.Same(DriverSchemaCatalog.DigitalActuator, new SysfsDigitalActuatorFactory().Schema);
        var chip = Assert.Single(DriverSchemaCatalog.DigitalActuator.Settings, s => s.Name == "chip");
        Assert.True(chip.HardwareOnly);
        Assert.Equal("0", chip.Default);
    }

    [Fact]
    public void The_host_composes_chardev_by_default_and_sysfs_on_request()
    {
        Assert.Contains("digital_actuator", MoundHost.HardwareDriverFactories().AvailableDriverTypes());
        Assert.True(MoundHost.HardwareDriverFactories(GpioBackings.Chardev).TryGet("digital_actuator", out var chardev));
        Assert.IsType<GpioChardevActuatorFactory>(chardev);
        Assert.True(MoundHost.HardwareDriverFactories(GpioBackings.Sysfs).TryGet("digital_actuator", out var sysfs));
        Assert.IsType<SysfsDigitalActuatorFactory>(sysfs);
        Assert.Throws<ArgumentException>(() => MoundHost.HardwareDriverFactories("libgpiod"));
    }
}
