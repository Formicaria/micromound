using System.Runtime.InteropServices;

namespace Micromound.Drivers;

/// <summary>
/// One I2C slave device on one bus, as the two operations a register-mapped chip needs: write a few
/// bytes to it, read a few bytes back. This is the seam a chip driver (<see cref="Ads1115AnalogInput"/>)
/// speaks its register protocol over, and it is deliberately this narrow so the whole protocol can be
/// exercised against a fake bus with no hardware — the same idea as <see cref="IDigitalOutput"/> under
/// the digital actuator. On a device it is <see cref="LinuxI2cBus"/> over <c>/dev/i2c-N</c>.
/// </summary>
public interface II2cBus
{
    /// <summary>Write these bytes to the device (a register address, optionally followed by data).</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Read exactly <c>buffer.Length</c> bytes from the device. Throws if the device does not answer.</summary>
    void Read(Span<byte> buffer);
}

/// <summary>
/// A real Linux I2C device over the kernel's <c>i2c-dev</c> interface: open <c>/dev/i2c-N</c>, select the
/// slave with the <c>I2C_SLAVE</c> ioctl, then plain <c>write(2)</c>/<c>read(2)</c> carry the transfers.
/// No library, no NuGet — three libc calls, like the directory fsync in the state store.
///
/// <para><b>Failures are I/O errors, not silence.</b> A bus that is not enabled (no <c>/dev/i2c-N</c>),
/// a chip that does not acknowledge its address (<c>EREMOTEIO</c>/<c>ENXIO</c>), or a permission
/// problem (the daemon must be in the <c>i2c</c> group) all surface as <see cref="IOException"/> with
/// the errno, so the driver above turns them into a fail-closed configuration refusal or a faulted
/// read — never a reading that happens to be zero.</para>
///
/// <para><b>On-device notes.</b> The Raspberry Pi's user-facing bus is <c>i2c-1</c> and must be enabled
/// (<c>dtparam=i2c_arm=on</c> / <c>raspi-config</c>). The transfers here must be verified against a
/// real chip; the protocol is proven against a fake bus.</para>
/// </summary>
public sealed class LinuxI2cBus : II2cBus, IDisposable
{
    private const int O_RDWR = 2;
    private const ulong I2C_SLAVE = 0x0703;

    private readonly int _fd;
    private readonly string _device;
    private readonly int _address;
    private bool _disposed;

    /// <param name="bus">The bus number: <c>/dev/i2c-{bus}</c>. The Pi's header bus is 1.</param>
    /// <param name="address">The 7-bit slave address, e.g. 0x48.</param>
    public LinuxI2cBus(int bus, int address)
    {
        if (bus < 0) throw new ArgumentOutOfRangeException(nameof(bus), bus, "an I2C bus number cannot be negative");
        if (address is < 0x03 or > 0x77) throw new ArgumentOutOfRangeException(nameof(address), address, "a 7-bit I2C address is 0x03..0x77");

        _device = $"/dev/i2c-{bus}";
        _address = address;
        _fd = Open(_device, O_RDWR);
        if (_fd < 0)
            throw new IOException($"cannot open {_device} (errno {Marshal.GetLastWin32Error()}); is the I2C bus enabled and is this user in the i2c group?");

        if (Ioctl(_fd, I2C_SLAVE, (ulong)address) < 0)
        {
            var errno = Marshal.GetLastWin32Error();
            Close(_fd);
            throw new IOException($"cannot select I2C address 0x{address:X2} on {_device} (errno {errno})");
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytes = data.ToArray();   // register transfers are 1–3 bytes; a copy is nothing
        var written = WriteFd(_fd, bytes, (nuint)bytes.Length);
        if (written != bytes.Length)
            throw new IOException($"I2C write to 0x{_address:X2} on {_device} failed (errno {Marshal.GetLastWin32Error()}); no acknowledge from the device?");
    }

    public void Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytes = new byte[buffer.Length];
        var read = ReadFd(_fd, bytes, (nuint)bytes.Length);
        if (read != bytes.Length)
            throw new IOException($"I2C read from 0x{_address:X2} on {_device} returned {read} of {buffer.Length} bytes (errno {Marshal.GetLastWin32Error()})");
        bytes.CopyTo(buffer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close(_fd);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, ulong request, ulong arg);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint WriteFd(int fd, byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadFd(int fd, [Out] byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}
