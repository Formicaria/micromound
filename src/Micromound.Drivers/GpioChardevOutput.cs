using System.Buffers.Binary;
using System.Text;

namespace Micromound.Drivers;

/// <summary>
/// A real Linux GPIO output line over the GPIO <b>character device</b> (<c>/dev/gpiochipN</c>, uapi v2)
/// — the interface libgpiod uses, and the one the kernel supports going forward; sysfs GPIO is
/// deprecated. No library: two ioctls on the chip and line descriptors, encoded by hand against the
/// kernel's <c>linux/gpio.h</c> layout (sizes and offsets pinned in tests from the header itself).
///
/// <para><b>How a line is claimed.</b> Open the chip, fill a <c>gpio_v2_line_request</c> for one line
/// offset as an OUTPUT with a <c>GPIO_V2_LINE_ATTR_ID_OUTPUT_VALUES</c> attribute carrying the level it
/// must start at, and issue <c>GPIO_V2_GET_LINE_IOCTL</c>. The kernel hands back a line descriptor and
/// the chip descriptor can be closed. Each write is one <c>GPIO_V2_LINE_SET_VALUES_IOCTL</c> on the line
/// descriptor. Closing the line descriptor releases it — and the kernel releases it too if the process
/// dies, which sysfs never did.</para>
///
/// <para><b>Requested at the safe level, atomically.</b> The initial level is part of the request, so
/// the line never spends an instant at the wrong level between "becomes an output" and "is written":
/// for an active-low relay board, requesting a line as a plain output (defaulting low) would energize
/// the load until the driver's first safe write — a real, if brief, glitch. The factory passes
/// <c>!active_high</c> here. A line another process already holds makes the request fail with
/// <c>EBUSY</c>, which surfaces as an <see cref="IOException"/> and a fail-closed configuration.</para>
///
/// <para><b>After release.</b> When the descriptor closes — <see cref="Dispose"/>, or the process
/// dying — the kernel returns the line to its default state (typically an input with the board's
/// pull), and nothing holds the level any more. Whether that idle state is safe is the board's
/// property, not this code's; SAFETY.md Layer 0. Supervision restarts the daemon, which requests the
/// line at the safe level again.</para>
///
/// <para><b>Chip numbering.</b> The Raspberry Pi's header lines are on <c>gpiochip0</c> on every model
/// with a current kernel (on a Pi 5 running an older 6.1/6.6 kernel they were <c>gpiochip4</c>); the
/// manifest's <c>chip</c> setting selects. Line offsets are BCM numbers on a Pi. The ioctls must be
/// verified on a board; the encoding is proven against the header and a fake.</para>
/// </summary>
public sealed class GpioChardevOutput : IDigitalOutput, IDisposable
{
    // linux/gpio.h (uapi v2). Sizes and offsets are the same on 32- and 64-bit: every field is a
    // fixed-width integer and the 64-bit ones are __aligned_u64.
    public const uint GetLineIoctl = 0xC250B407;     // _IOWR(0xB4, 0x07, struct gpio_v2_line_request)  — 592 bytes
    public const uint SetValuesIoctl = 0xC010B40F;   // _IOWR(0xB4, 0x0F, struct gpio_v2_line_values)   — 16 bytes
    public const int LineRequestSize = 592;
    public const int LineValuesSize = 16;
    public const ulong FlagOutput = 1UL << 3;        // GPIO_V2_LINE_FLAG_OUTPUT
    public const uint AttrIdOutputValues = 2;        // GPIO_V2_LINE_ATTR_ID_OUTPUT_VALUES
    private const int OffsetConsumer = 256;          // char consumer[32]
    private const int OffsetConfigFlags = 288;       // config.flags
    private const int OffsetConfigNumAttrs = 296;    // config.num_attrs
    private const int OffsetConfigAttrs = 320;       // config.attrs[0]  (attr.id @+0, attr.values @+8, mask @+16)
    private const int OffsetNumLines = 560;
    private const int OffsetFd = 588;

    /// <summary>The consumer label the kernel shows in <c>gpioinfo</c> for lines this daemon holds.</summary>
    public const string Consumer = "micromound";

    private readonly ILinuxIo _io;
    private readonly int _lineFd;
    private readonly string _device;
    private readonly int _line;
    private bool _disposed;

    /// <param name="line">The line offset on the chip (a BCM GPIO number on a Raspberry Pi).</param>
    /// <param name="initialHigh">The level the line is driven to as it becomes an output — the SAFE level.</param>
    /// <param name="chip">The chip number: <c>/dev/gpiochip{chip}</c>.</param>
    /// <param name="io">The system-call seam; libc on a device, a fake in tests.</param>
    public GpioChardevOutput(int line, bool initialHigh, int chip = 0, ILinuxIo? io = null)
    {
        if (line < 0) throw new ArgumentOutOfRangeException(nameof(line), line, "a GPIO line offset cannot be negative");
        if (chip < 0) throw new ArgumentOutOfRangeException(nameof(chip), chip, "a GPIO chip number cannot be negative");

        _io = io ?? LibcIo.Instance;
        _line = line;
        _device = $"/dev/gpiochip{chip}";

        var chipFd = _io.Open(_device, LibcIo.O_RDWR | LibcIo.O_CLOEXEC);
        if (chipFd < 0)
            throw new IOException($"cannot open {_device} (errno {_io.LastErrno()}); is this the right chip, and is this user in the gpio group?");

        try
        {
            var request = BuildLineRequest(line, initialHigh, Consumer);
            if (_io.Ioctl(chipFd, GetLineIoctl, request) < 0)
            {
                var errno = _io.LastErrno();
                throw new IOException($"cannot claim GPIO line {line} on {_device} as an output (errno {errno}" +
                                      (errno == 16 ? ", EBUSY: another process holds it" : "") + ")");
            }
            _lineFd = RequestedFd(request);
            if (_lineFd < 0)
                throw new IOException($"the kernel returned no descriptor for GPIO line {line} on {_device}");
        }
        finally
        {
            _io.Close(chipFd);   // the line descriptor stands on its own
        }

        State = initialHigh;
    }

    public bool State { get; private set; }

    public void Write(bool high)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_io.Ioctl(_lineFd, SetValuesIoctl, BuildLineValues(high)) < 0)
            throw new IOException($"cannot set GPIO line {_line} on {_device} {(high ? "high" : "low")} (errno {_io.LastErrno()})");
        State = high;
    }

    /// <summary>Releases the line (the kernel also releases it if the process dies).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _io.Close(_lineFd);
    }

    /// <summary>
    /// A <c>gpio_v2_line_request</c> for ONE line: <c>offsets[0]</c>, the consumer label, config flags
    /// OUTPUT, one config attribute (OUTPUT_VALUES, <c>values</c> bit 0 = the initial level, <c>mask</c>
    /// bit 0 = it applies to our one line), <c>num_lines</c> 1. Public so a test can pin the layout.
    /// </summary>
    public static byte[] BuildLineRequest(int line, bool initialHigh, string consumer)
    {
        if (line < 0) throw new ArgumentOutOfRangeException(nameof(line));
        var buffer = new byte[LineRequestSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)line);          // offsets[0]

        var label = Encoding.ASCII.GetBytes(consumer);
        var n = Math.Min(label.Length, 31);                                                  // NUL-terminated within 32
        label.AsSpan(0, n).CopyTo(buffer.AsSpan(OffsetConsumer, n));

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(OffsetConfigFlags, 8), FlagOutput);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OffsetConfigNumAttrs, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OffsetConfigAttrs + 0, 4), AttrIdOutputValues);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(OffsetConfigAttrs + 8, 8), initialHigh ? 1UL : 0UL);   // values
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(OffsetConfigAttrs + 16, 8), 1UL);                      // mask: line 0 of the request

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OffsetNumLines, 4), 1);
        return buffer;
    }

    /// <summary>The <c>fd</c> the kernel wrote into a completed line request.</summary>
    public static int RequestedFd(byte[] request) => BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(OffsetFd, 4));

    /// <summary>A <c>gpio_v2_line_values</c> setting our one line: <c>bits</c> bit 0 = level, <c>mask</c> bit 0 = 1.</summary>
    public static byte[] BuildLineValues(bool high)
    {
        var buffer = new byte[LineValuesSize];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(0, 8), high ? 1UL : 0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8, 8), 1UL);
        return buffer;
    }

    // ---- decoding, for tests and diagnostics ----

    /// <summary>Reads the fields <see cref="BuildLineRequest"/> writes, from a request buffer.</summary>
    public static (uint Line, string Consumer, ulong Flags, uint NumAttrs, uint AttrId, ulong AttrValues, ulong AttrMask, uint NumLines) DecodeLineRequest(byte[] request)
    {
        if (request.Length != LineRequestSize) throw new ArgumentException($"a line request is {LineRequestSize} bytes, not {request.Length}");
        var consumerBytes = request.AsSpan(OffsetConsumer, 32);
        var end = consumerBytes.IndexOf((byte)0);
        return (
            BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(0, 4)),
            Encoding.ASCII.GetString(consumerBytes[..(end < 0 ? 32 : end)]),
            BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(OffsetConfigFlags, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(OffsetConfigNumAttrs, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(OffsetConfigAttrs, 4)),
            BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(OffsetConfigAttrs + 8, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(OffsetConfigAttrs + 16, 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(OffsetNumLines, 4)));
    }

    /// <summary>Reads <c>bits</c> and <c>mask</c> from a line-values buffer.</summary>
    public static (ulong Bits, ulong Mask) DecodeLineValues(byte[] values)
    {
        if (values.Length != LineValuesSize) throw new ArgumentException($"line values are {LineValuesSize} bytes, not {values.Length}");
        return (BinaryPrimitives.ReadUInt64LittleEndian(values.AsSpan(0, 8)), BinaryPrimitives.ReadUInt64LittleEndian(values.AsSpan(8, 8)));
    }

    /// <summary>Writes the kernel's reply — the line descriptor — into a request, as the kernel would. For fakes.</summary>
    public static void WriteRequestedFd(byte[] request, int fd) => BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(OffsetFd, 4), fd);
}
