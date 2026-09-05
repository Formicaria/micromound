using Micromound.Capabilities;
using Micromound.Drivers;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The first real backing for the analog seam: <see cref="Ads1115AnalogInput"/> speaking the ADS1115's
/// register protocol over an <see cref="II2cBus"/>, the calibrating <see cref="AnalogSensorDriver"/>
/// above it, and the <see cref="Ads1115AnalogSensorFactory"/> that reads the chip's location from the
/// manifest. These run against a FAKE chip — a register file with the datasheet's behaviour (OS bit
/// cleared while converting, two's-complement conversion result) — so the protocol, the encoding, the
/// volts scaling, and every fail-closed path are proven with no hardware; the I2C transfers themselves
/// are what must still be verified on a real board.
/// </summary>
public sealed class Ads1115Tests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-03T12:00:00Z");

    /// <summary>
    /// A fake ADS1115 at the register level. A write of one byte selects a register; a write of three
    /// sets the Config register (and, if OS is set, starts a conversion that completes after
    /// <see cref="ConversionPolls"/> polls); a read returns the selected register big-endian. It records
    /// every config word written so a test can pin the exact encoding the driver sends.
    /// </summary>
    private sealed class FakeAds1115 : II2cBus
    {
        public bool Present { get; set; } = true;
        public short Conversion { get; set; }
        public int ConversionPolls { get; set; } = 1;   // polls of Config before OS reads back 1
        public List<ushort> ConfigWrites { get; } = [];
        public int Reads { get; private set; }
        public int Writes { get; private set; }

        private byte _pointer;
        private ushort _config = 0x8583;   // datasheet power-on default: idle, AIN0/AIN1, ±2.048, single-shot
        private int _pollsLeft;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (!Present) throw new IOException("I2C write failed (errno 121): no acknowledge");
            Writes++;
            _pointer = data[0];
            if (data.Length == 3 && _pointer == 0x01)
            {
                var word = (ushort)((data[1] << 8) | data[2]);
                ConfigWrites.Add(word);
                _config = word;
                if ((word & 0x8000) != 0)
                {
                    _pollsLeft = ConversionPolls;
                    _config &= 0x7FFF;   // the chip clears OS while converting
                }
            }
        }

        public void Read(Span<byte> buffer)
        {
            if (!Present) throw new IOException("I2C read failed (errno 121): no acknowledge");
            Reads++;
            ushort value;
            switch (_pointer)
            {
                case 0x00:
                    value = (ushort)Conversion;
                    break;
                case 0x01:
                    if (_pollsLeft > 0 && --_pollsLeft == 0)
                        _config |= 0x8000;   // conversion complete
                    value = _config;
                    break;
                default:
                    value = 0;
                    break;
            }
            buffer[0] = (byte)(value >> 8);
            buffer[1] = (byte)(value & 0xFF);
        }
    }

    private static CapabilityExecution Sample(string capability) => new()
    {
        CapabilityId = capability,
        Parameters = new Dictionary<string, double>(),
        StartedAt = Now,
        EffectiveLimits = new CapabilityLimits()
    };

    // ---- the chip protocol ----

    [Fact]
    public void Channel_0_at_4V096_encodes_the_datasheet_config_word()
    {
        var chip = new FakeAds1115();
        var input = new Ads1115AnalogInput(chip, channel: 0, fullScaleVolts: 4.096);

        // OS=1, MUX=100 (AIN0/GND), PGA=001 (±4.096), MODE=1, DR=100 (128 SPS), COMP_QUE=11.
        Assert.Equal(0xC383, input.ConfigWord);
    }

    [Theory]
    [InlineData(0, 6.144, 0xC183)]
    [InlineData(1, 4.096, 0xD383)]
    [InlineData(2, 2.048, 0xE583)]
    [InlineData(3, 0.256, 0xFB83)]
    public void Every_channel_and_range_lands_in_the_right_bits(int channel, double range, int expected)
    {
        var input = new Ads1115AnalogInput(new FakeAds1115(), channel, range);
        Assert.Equal((ushort)expected, input.ConfigWord);
    }

    [Fact]
    public void A_read_writes_config_waits_for_the_conversion_and_scales_the_result_to_volts()
    {
        var chip = new FakeAds1115 { Conversion = 16384, ConversionPolls = 3 };   // half of full scale
        var input = new Ads1115AnalogInput(chip, channel: 0, fullScaleVolts: 4.096);

        var volts = input.Read();

        Assert.Equal(2.048, volts, precision: 9);
        Assert.Equal([0xC383], chip.ConfigWrites);   // exactly one conversion started
    }

    [Fact]
    public void A_negative_conversion_is_read_as_twos_complement()
    {
        var chip = new FakeAds1115 { Conversion = -8192 };
        var input = new Ads1115AnalogInput(chip, channel: 1, fullScaleVolts: 2.048);

        Assert.Equal(-0.512, input.Read(), precision: 9);
    }

    [Fact]
    public void Full_scale_is_one_lsb_short_of_the_range()
    {
        var chip = new FakeAds1115 { Conversion = short.MaxValue };
        var input = new Ads1115AnalogInput(chip, channel: 0, fullScaleVolts: 4.096);

        Assert.Equal(4.096 * 32767 / 32768, input.Read(), precision: 9);
    }

    [Fact]
    public void Each_read_is_a_fresh_single_shot_conversion()
    {
        var chip = new FakeAds1115 { Conversion = 1000 };
        var input = new Ads1115AnalogInput(chip, channel: 0);

        input.Read();
        chip.Conversion = 2000;
        var second = input.Read();

        Assert.Equal(2, chip.ConfigWrites.Count);
        Assert.Equal(2000 * 4.096 / 32768, second, precision: 9);
    }

    [Fact]
    public void A_conversion_that_never_completes_is_an_io_error_not_a_stale_number()
    {
        var chip = new FakeAds1115 { Conversion = 1234, ConversionPolls = 100 };
        var input = new Ads1115AnalogInput(chip, channel: 0, maxPolls: 5);

        var ex = Assert.Throws<IOException>(() => input.Read());
        Assert.Contains("did not complete", ex.Message);
    }

    [Fact]
    public void A_missing_chip_fails_at_construction()
    {
        var chip = new FakeAds1115 { Present = false };

        Assert.Throws<IOException>(() => new Ads1115AnalogInput(chip, channel: 0));
    }

    [Fact]
    public void Construction_probes_the_chip_once()
    {
        var chip = new FakeAds1115();
        _ = new Ads1115AnalogInput(chip, channel: 0);

        Assert.Equal(1, chip.Reads);
        Assert.Empty(chip.ConfigWrites);   // a probe reads; it never starts a conversion
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void A_channel_the_chip_does_not_have_is_refused(int channel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ads1115AnalogInput(new FakeAds1115(), channel));
    }

    [Fact]
    public void A_range_the_pga_does_not_offer_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Ads1115AnalogInput(new FakeAds1115(), 0, fullScaleVolts: 3.3));
    }

    // ---- the sensor driver over the real channel ----

    [Fact]
    public void The_sensor_driver_samples_the_chip_and_emits_volts_as_evidence()
    {
        var chip = new FakeAds1115 { Conversion = 16384 };
        var driver = new AnalogSensorDriver(_ => new Ads1115AnalogInput(chip, channel: 0));
        EvidenceItem? published = null;
        driver.Publish = item => published = item;

        var configured = driver.Configure(new Dictionary<string, string> { ["capability"] = "sense.soil_moisture", ["unit"] = "V" });
        Assert.True(configured.IsValid);

        var outcome = driver.Executors[0].Execute(Sample("sense.soil_moisture"));

        Assert.True(outcome.Succeeded);
        var evidence = Assert.Single(outcome.Evidence);
        Assert.Same(evidence, published);
        Assert.True(EvidenceReadings.TryRead(evidence, out var value));
        Assert.Equal(2.048, value, precision: 9);
    }

    [Fact]
    public void Scale_and_offset_map_volts_into_the_sensors_unit()
    {
        var chip = new FakeAds1115 { Conversion = 16384 };   // 2.048 V
        var driver = new AnalogSensorDriver(_ => new Ads1115AnalogInput(chip, channel: 0));
        driver.Configure(new Dictionary<string, string>
        {
            ["capability"] = "sense.soil_moisture",
            ["unit"] = "pct",
            ["scale"] = "50",     // 0..2 V → 0..100 %
            ["offset"] = "-2.4"
        });

        var outcome = driver.Executors[0].Execute(Sample("sense.soil_moisture"));

        Assert.True(EvidenceReadings.TryRead(Assert.Single(outcome.Evidence), out var value));
        Assert.Equal(2.048 * 50 - 2.4, value, precision: 9);
        Assert.Equal(50, driver.Scale);
        Assert.Equal(-2.4, driver.Offset);
    }

    [Theory]
    [InlineData("scale", "NaN")]
    [InlineData("scale", "Infinity")]
    [InlineData("offset", "-Infinity")]
    [InlineData("offset", "two")]
    public void A_non_finite_or_non_numeric_calibration_fails_closed(string key, string raw)
    {
        var driver = new AnalogSensorDriver(new InMemoryAnalogInput());
        var result = driver.Configure(new Dictionary<string, string> { ["capability"] = "sense.x", [key] = raw });

        Assert.False(result.IsValid);
        Assert.Equal(DriverHealth.Absent, driver.Health);
        Assert.Empty(driver.Executors);
    }

    [Fact]
    public void A_chip_that_is_not_there_refuses_configuration_and_exposes_nothing()
    {
        var chip = new FakeAds1115 { Present = false };
        var driver = new AnalogSensorDriver(_ => new Ads1115AnalogInput(chip, channel: 0));

        var result = driver.Configure(new Dictionary<string, string> { ["capability"] = "sense.soil_moisture" });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("could not open the sensor's channel"));
        Assert.Equal(DriverHealth.Absent, driver.Health);
        Assert.Empty(driver.Capabilities);
        Assert.Empty(driver.Executors);
    }

    [Fact]
    public void A_chip_that_stops_answering_faults_the_read_with_no_reading()
    {
        var chip = new FakeAds1115 { Conversion = 100 };
        var driver = new AnalogSensorDriver(_ => new Ads1115AnalogInput(chip, channel: 0));
        var published = 0;
        driver.Publish = _ => published++;
        driver.Configure(new Dictionary<string, string> { ["capability"] = "sense.soil_moisture" });

        chip.Present = false;   // unplugged after bring-up
        var outcome = driver.Executors[0].Execute(Sample("sense.soil_moisture"));

        Assert.False(outcome.Succeeded);
        Assert.Contains("sensor read failed", outcome.Detail);
        Assert.Empty(outcome.Evidence);   // a fault carries no fabricated number
        Assert.Equal(0, published);
    }

    [Fact]
    public void The_channel_is_opened_once_per_configure_and_dropped_on_a_failed_reconfigure()
    {
        var opened = 0;
        var driver = new AnalogSensorDriver(_ => { opened++; return new InMemoryAnalogInput { Value = 1 }; });

        Assert.True(driver.Configure(new Dictionary<string, string> { ["capability"] = "sense.a" }).IsValid);
        Assert.Equal(1, opened);

        Assert.False(driver.Configure(new Dictionary<string, string>()).IsValid);   // bad slice: builder never runs
        Assert.Equal(1, opened);
        Assert.Empty(driver.Executors);
    }

    // ---- the hardware factory: manifest settings → chip location ----

    [Fact]
    public void The_factory_opens_the_chip_the_manifest_names()
    {
        var opened = new List<(int Bus, int Address)>();
        var chip = new FakeAds1115 { Conversion = 8192 };
        var factory = new Ads1115AnalogSensorFactory(defaultBus: 1, busFactory: (bus, address) => { opened.Add((bus, address)); return chip; });

        Assert.Equal("analog_sensor", factory.DriverType);
        var driver = factory.Create();
        var result = driver.Configure(new Dictionary<string, string>
        {
            ["capability"] = "sense.tank_level",
            ["bus"] = "3",
            ["address"] = "0x49",
            ["channel"] = "2",
            ["gain"] = "2.048"
        });

        Assert.True(result.IsValid);
        Assert.Equal([(3, 0x49)], opened);
        Assert.Empty(chip.ConfigWrites);   // nothing yet; the probe only reads

        var outcome = driver.Executors[0].Execute(Sample("sense.tank_level"));
        Assert.True(outcome.Succeeded);
        Assert.Equal([(ushort)0xE583], chip.ConfigWrites);   // AIN2, ±2.048
        Assert.True(EvidenceReadings.TryRead(Assert.Single(outcome.Evidence), out var value));
        Assert.Equal(0.512, value, precision: 9);
    }

    [Fact]
    public void Bus_address_and_gain_default_to_the_pi_header_bus_0x48_and_4V096()
    {
        var opened = new List<(int Bus, int Address)>();
        var chip = new FakeAds1115();
        var factory = new Ads1115AnalogSensorFactory(busFactory: (bus, address) => { opened.Add((bus, address)); return chip; });

        var driver = factory.Create();
        Assert.True(driver.Configure(new Dictionary<string, string> { ["capability"] = "sense.x", ["channel"] = "0" }).IsValid);
        driver.Executors[0].Execute(Sample("sense.x"));

        Assert.Equal([(1, 0x48)], opened);
        Assert.Equal([(ushort)0xC383], chip.ConfigWrites);
    }

    [Fact]
    public void A_decimal_address_is_accepted_too()
    {
        var opened = new List<(int Bus, int Address)>();
        var factory = new Ads1115AnalogSensorFactory(busFactory: (bus, address) => { opened.Add((bus, address)); return new FakeAds1115(); });

        var driver = factory.Create();
        Assert.True(driver.Configure(new Dictionary<string, string> { ["capability"] = "sense.x", ["channel"] = "1", ["address"] = "73" }).IsValid);

        Assert.Equal([(1, 0x49)], opened);
    }

    [Theory]
    [InlineData("channel", null)]         // required
    [InlineData("channel", "4")]          // the chip has 0..3
    [InlineData("channel", "AIN0")]       // not an integer
    [InlineData("address", "0x")]         // malformed hex
    [InlineData("address", "0x100")]      // not a 7-bit address (refused by the bus)
    [InlineData("bus", "one")]
    [InlineData("gain", "3.3")]           // not a PGA range
    [InlineData("gain", "high")]
    public void A_malformed_chip_location_fails_closed_and_opens_nothing(string key, string? raw)
    {
        var opened = 0;
        var factory = new Ads1115AnalogSensorFactory(busFactory: (bus, address) =>
        {
            opened++;
            if (address is < 0x03 or > 0x77) throw new ArgumentOutOfRangeException(nameof(address));   // what LinuxI2cBus does
            return new FakeAds1115();
        });
        var settings = new Dictionary<string, string> { ["capability"] = "sense.x", ["channel"] = "0" };
        if (raw is null) settings.Remove(key); else settings[key] = raw;

        var driver = factory.Create();
        var result = driver.Configure(settings);

        Assert.False(result.IsValid);
        Assert.Equal(DriverHealth.Absent, driver.Health);
        Assert.Empty(driver.Executors);
        if (key != "address") Assert.Equal(0, opened);   // a bad setting is refused before any bus is touched
    }

    [Fact]
    public void A_chip_that_does_not_acknowledge_refuses_bring_up_through_the_factory()
    {
        var factory = new Ads1115AnalogSensorFactory(busFactory: (_, _) => new FakeAds1115 { Present = false });

        var driver = factory.Create();
        var result = driver.Configure(new Dictionary<string, string> { ["capability"] = "sense.x", ["channel"] = "0" });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no acknowledge"));
        Assert.Equal(DriverHealth.Absent, driver.Health);
    }

    [Fact]
    public void Each_created_driver_opens_its_own_chip()
    {
        var opened = 0;
        var factory = new Ads1115AnalogSensorFactory(busFactory: (_, _) => { opened++; return new FakeAds1115(); });

        var a = factory.Create();
        var b = factory.Create();
        a.Configure(new Dictionary<string, string> { ["capability"] = "sense.a", ["channel"] = "0" });
        b.Configure(new Dictionary<string, string> { ["capability"] = "sense.b", ["channel"] = "1" });

        Assert.Equal(2, opened);
    }

    [Fact]
    public void The_linux_bus_refuses_an_impossible_address_before_touching_the_device_node()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinuxI2cBus(1, 0x100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinuxI2cBus(-1, 0x48));
    }
}
