namespace Micromound.Drivers;

/// <summary>
/// One single-ended channel of a Texas Instruments ADS1115 — the 16-bit, four-channel I2C ADC that is
/// the usual analog front end on a Raspberry Pi — as an <see cref="IAnalogInput"/>. This is the first
/// real backing for the sensor seam, the counterpart of the GPIO port under the actuator: the generic
/// <see cref="AnalogSensorDriver"/> reads it and knows nothing about registers.
///
/// <para><b>The protocol, single-shot.</b> Each <see cref="Read"/> writes the 16-bit Config register
/// (0x01): OS=1 to start one conversion, MUX selecting AINn against GND, the PGA for the chosen
/// full-scale range, MODE=1 single-shot, 128 SPS, comparator disabled. It then polls Config until OS
/// reads back 1 (conversion complete — the chip clears it while converting) and reads the 16-bit
/// two's-complement Conversion register (0x00). The result is returned in <b>volts</b>:
/// <c>raw × FSR / 32768</c>. Single-shot means the chip sleeps between reads and every value is a fresh
/// sample taken when the mound asked for it, which is what a reading used as evidence should be.</para>
///
/// <para><b>Fail closed on a missing chip.</b> Construction probes the Config register once; a chip
/// that does not acknowledge makes the constructor throw, which the sensor driver turns into a
/// configuration refusal — so a manifest pointing at an empty address never yields a stream of zeros
/// that look like readings. A failed read later throws too, and the executor reports a fault.</para>
///
/// <para><b>Ranges.</b> The PGA full-scale range is a <em>gain</em> setting, not a protection: the
/// ADS1115's inputs must never exceed VDD + 0.3 V whatever the range. ±4.096 V is the sane default
/// for a 3.3 V system (a 3.3 V signal sits inside it with headroom); ±6.144 V does not add input
/// range on a 3.3 V supply, it only coarsens the LSB. The transfers themselves must be verified on a
/// real board; the protocol is proven against a fake bus.</para>
/// </summary>
public sealed class Ads1115AnalogInput : IAnalogInput, IDisposable
{
    /// <summary>The default 7-bit address (ADDR pin to GND). ADDR to VDD/SDA/SCL gives 0x49/0x4A/0x4B.</summary>
    public const int DefaultAddress = 0x48;

    private const byte ConversionRegister = 0x00;
    private const byte ConfigRegister = 0x01;

    // Config register fields (ADS1115 datasheet §9.6.3).
    private const ushort OsStartOrIdle = 0x8000;      // write: begin single conversion; read: 1 = not converting
    private const ushort ModeSingleShot = 0x0100;
    private const ushort DataRate128Sps = 0x0080;     // DR = 100
    private const ushort ComparatorDisabled = 0x0003; // COMP_QUE = 11

    /// <summary>The PGA full-scale ranges the chip offers, in volts, indexed by the 3-bit PGA code.</summary>
    public static readonly IReadOnlyList<double> FullScaleRanges = [6.144, 4.096, 2.048, 1.024, 0.512, 0.256];

    private readonly II2cBus _bus;
    private readonly int _channel;
    private readonly int _pgaCode;
    private readonly int _maxPolls;

    /// <summary>The full-scale range in force, in volts.</summary>
    public double FullScaleVolts => FullScaleRanges[_pgaCode];

    /// <param name="bus">The device on its bus (a <see cref="LinuxI2cBus"/> on a Pi; a fake in tests).</param>
    /// <param name="channel">Single-ended input AIN0..AIN3.</param>
    /// <param name="fullScaleVolts">One of <see cref="FullScaleRanges"/>. Defaults to 4.096.</param>
    /// <param name="maxPolls">How many times to poll for conversion-complete before giving up. Polls are
    /// paced ≥1 ms apart, so this is also a floor on the wait in milliseconds; at 128 SPS a conversion
    /// takes ~8 ms, so the default 50 is generous without letting a dead chip stall a tick.</param>
    public Ads1115AnalogInput(II2cBus bus, int channel, double fullScaleVolts = 4.096, int maxPolls = 50)
    {
        ArgumentNullException.ThrowIfNull(bus);
        if (channel is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "the ADS1115 has single-ended channels 0..3");
        var pga = IndexOfRange(fullScaleVolts);
        if (pga < 0)
            throw new ArgumentOutOfRangeException(nameof(fullScaleVolts), fullScaleVolts,
                "full-scale range must be one of " + string.Join(", ", FullScaleRanges));
        if (maxPolls < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPolls), maxPolls, "at least one poll is needed");

        _bus = bus;
        _channel = channel;
        _pgaCode = pga;
        _maxPolls = maxPolls;

        // Probe: read Config once. A chip that is not there does not acknowledge, the bus throws, and
        // the driver above refuses to configure — fail closed, never a phantom sensor.
        ReadRegister(ConfigRegister);
    }

    /// <summary>The Config word a read of this channel writes — exposed so a test can pin the encoding.</summary>
    public ushort ConfigWord =>
        (ushort)(OsStartOrIdle
                 | (ushort)((0b100 + _channel) << 12)   // MUX: 100..111 = AIN0..AIN3 vs GND
                 | (ushort)(_pgaCode << 9)
                 | ModeSingleShot
                 | DataRate128Sps
                 | ComparatorDisabled);

    public double Read()
    {
        var config = ConfigWord;
        _bus.Write([ConfigRegister, (byte)(config >> 8), (byte)(config & 0xFF)]);

        // The chip clears OS while converting and sets it when the result is ready. A conversion at
        // 128 SPS takes ~8 ms; polls are paced at ≥1 ms so the budget is ≥ maxPolls milliseconds
        // regardless of bus speed (back-to-back polls on a 400 kHz bus would burn 50 in ~6 ms).
        var ready = false;
        for (var i = 0; i < _maxPolls && !ready; i++)
        {
            ready = (ReadRegister(ConfigRegister) & OsStartOrIdle) != 0;
            if (!ready && i + 1 < _maxPolls)
                Thread.Sleep(1);
        }
        if (!ready)
            throw new IOException($"ADS1115 channel {_channel}: conversion did not complete after {_maxPolls} polls");

        var raw = (short)ReadRegister(ConversionRegister);   // two's complement, big-endian on the wire
        return raw * FullScaleVolts / 32768.0;
    }

    private ushort ReadRegister(byte register)
    {
        _bus.Write([register]);
        Span<byte> buffer = stackalloc byte[2];
        _bus.Read(buffer);
        return (ushort)((buffer[0] << 8) | buffer[1]);
    }

    /// <summary>Closes the underlying device node if the bus owns one (<see cref="LinuxI2cBus"/> does).</summary>
    public void Dispose() => (_bus as IDisposable)?.Dispose();

    /// <summary>The PGA code for a full-scale range, or -1 if it is not one the chip offers.</summary>
    public static int IndexOfRange(double fullScaleVolts)
    {
        for (var i = 0; i < FullScaleRanges.Count; i++)
            if (Math.Abs(FullScaleRanges[i] - fullScaleVolts) < 1e-9)
                return i;
        return -1;
    }
}
