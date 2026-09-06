using System.Buffers.Binary;
using System.Text;

namespace Micromound.Drivers;

/// <summary>
/// A real Linux GPIO <b>input</b> line over the GPIO character device (<c>/dev/gpiochipN</c>, uapi v2)
/// — the read side of <see cref="GpioChardevOutput"/>, claimed and sampled through the same two ioctl
/// shapes and the same <see cref="ILinuxIo"/> seam. This is the line a limit switch, an interlock
/// contact or a float sits on: the thing that lets a mound confirm an actuation with something other
/// than its own command.
///
/// <para><b>How a line is claimed.</b> Open the chip, fill a <c>gpio_v2_line_request</c> for one line
/// offset with the INPUT flag and the requested bias (pull-up, pull-down, or none — a dry contact needs
/// a bias or it floats and reads as noise), and issue <c>GPIO_V2_GET_LINE_IOCTL</c>. Each sample is one
/// <c>GPIO_V2_LINE_GET_VALUES_IOCTL</c> on the line descriptor. No attribute carries an initial value:
/// an input has no level to set, which is precisely why this request is shorter than the output's.</para>
///
/// <para><b>A read that fails throws.</b> A chip that stopped answering, or a descriptor the kernel
/// revoked, must not read as <c>false</c> — "the switch is open" and "I could not see the switch" are
/// different facts, and the sensor driver above turns the exception into a fault with no reading rather
/// than a measurement (SAFETY.md; PROTOCOL.md §6).</para>
///
/// <para>The ioctl encoding is proven against the header and a fake here, and must be verified on a
/// board — the same standing caveat as the output line.</para>
/// </summary>
public sealed class GpioChardevInput : IDigitalInput, IDisposable
{
    // linux/gpio.h (uapi v2), the same structures the output line uses.
    public const uint GetLineIoctl = GpioChardevOutput.GetLineIoctl;
    public const uint GetValuesIoctl = 0xC010B40E;    // _IOWR(0xB4, 0x0E, struct gpio_v2_line_values) — 16 bytes
    public const ulong FlagInput = 1UL << 2;          // GPIO_V2_LINE_FLAG_INPUT
    public const ulong FlagBiasPullUp = 1UL << 8;     // GPIO_V2_LINE_FLAG_BIAS_PULL_UP
    public const ulong FlagBiasPullDown = 1UL << 9;   // GPIO_V2_LINE_FLAG_BIAS_PULL_DOWN
    public const ulong FlagBiasDisabled = 1UL << 7;   // GPIO_V2_LINE_FLAG_BIAS_DISABLED
    private const int OffsetConsumer = 256;
    private const int OffsetConfigFlags = 288;
    private const int OffsetNumLines = 560;
    private const int OffsetFd = 588;

    private readonly ILinuxIo _io;
    private readonly int _lineFd;
    private readonly string _device;
    private readonly int _line;
    private bool _disposed;

    /// <param name="line">The line offset on the chip (a BCM GPIO number on a Raspberry Pi).</param>
    /// <param name="bias">One of <see cref="GpioBias"/>: what holds the line when nothing drives it.</param>
    /// <param name="chip">The chip number: <c>/dev/gpiochip{chip}</c>.</param>
    /// <param name="io">The system-call seam; libc on a device, a fake in tests.</param>
    public GpioChardevInput(int line, string bias = GpioBias.PullUp, int chip = 0, ILinuxIo? io = null)
    {
        if (line < 0) throw new ArgumentOutOfRangeException(nameof(line), line, "a GPIO line offset cannot be negative");
        if (chip < 0) throw new ArgumentOutOfRangeException(nameof(chip), chip, "a GPIO chip number cannot be negative");
        if (!GpioBias.IsKnown(bias)) throw new ArgumentException($"'{bias}' is not a bias; use {GpioBias.PullUp}, {GpioBias.PullDown} or {GpioBias.None}", nameof(bias));

        _io = io ?? LibcIo.Instance;
        _line = line;
        _device = $"/dev/gpiochip{chip}";

        var chipFd = _io.Open(_device, LibcIo.O_RDWR | LibcIo.O_CLOEXEC);
        if (chipFd < 0)
            throw new IOException($"cannot open {_device} (errno {_io.LastErrno()}); is this the right chip, and is this user in the gpio group?");

        try
        {
            var request = BuildLineRequest(line, bias, GpioChardevOutput.Consumer);
            if (_io.Ioctl(chipFd, GetLineIoctl, request) < 0)
            {
                var errno = _io.LastErrno();
                throw new IOException($"cannot claim GPIO line {line} on {_device} as an input (errno {errno}" +
                                      (errno == 16 ? ", EBUSY: another process holds it" : "") + ")");
            }
            _lineFd = BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(OffsetFd, 4));
            if (_lineFd < 0)
                throw new IOException($"the kernel returned no descriptor for GPIO line {line} on {_device}");
        }
        finally
        {
            _io.Close(chipFd);
        }
    }

    /// <summary>Samples the line. Throws when the kernel refuses — never a level the hardware did not give.</summary>
    public bool Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var values = BuildLineValues();
        if (_io.Ioctl(_lineFd, GetValuesIoctl, values) < 0)
            throw new IOException($"cannot read GPIO line {_line} on {_device} (errno {_io.LastErrno()})");
        return (BinaryPrimitives.ReadUInt64LittleEndian(values.AsSpan(0, 8)) & 1UL) != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _io.Close(_lineFd);
    }

    /// <summary>
    /// A <c>gpio_v2_line_request</c> for ONE input line: <c>offsets[0]</c>, the consumer label, config
    /// flags INPUT plus the bias, <c>num_lines</c> 1, and no attributes — an input has no value to set.
    /// Public so a test can pin the layout against the header.
    /// </summary>
    public static byte[] BuildLineRequest(int line, string bias, string consumer)
    {
        if (line < 0) throw new ArgumentOutOfRangeException(nameof(line));
        var buffer = new byte[GpioChardevOutput.LineRequestSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)line);

        var label = Encoding.ASCII.GetBytes(consumer);
        var n = Math.Min(label.Length, 31);
        label.AsSpan(0, n).CopyTo(buffer.AsSpan(OffsetConsumer, n));

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(OffsetConfigFlags, 8), FlagInput | BiasFlag(bias));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OffsetNumLines, 4), 1);
        return buffer;
    }

    /// <summary>A <c>gpio_v2_line_values</c> asking for our one line: <c>bits</c> zeroed, <c>mask</c> bit 0.</summary>
    public static byte[] BuildLineValues()
    {
        var buffer = new byte[GpioChardevOutput.LineValuesSize];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8, 8), 1UL);
        return buffer;
    }

    public static ulong BiasFlag(string bias) => bias switch
    {
        GpioBias.PullUp => FlagBiasPullUp,
        GpioBias.PullDown => FlagBiasPullDown,
        _ => FlagBiasDisabled
    };

    /// <summary>Writes a sampled level into a values buffer, as the kernel would. For fakes.</summary>
    public static void WriteSampledLevel(byte[] values, bool high) =>
        BinaryPrimitives.WriteUInt64LittleEndian(values.AsSpan(0, 8), high ? 1UL : 0UL);
}

/// <summary>
/// What holds an input line when nothing is driving it. A dry contact to ground wants
/// <see cref="PullUp"/> (the default, and the wiring of nearly every limit switch); a contact to the
/// supply wants <see cref="PullDown"/>; a line already biased on the board wants <see cref="None"/>.
/// A floating input is not a reading — it is noise that looks like one.
/// </summary>
public static class GpioBias
{
    public const string PullUp = "pull_up";
    public const string PullDown = "pull_down";
    public const string None = "none";

    public static bool IsKnown(string? bias) => bias is PullUp or PullDown or None;
}
