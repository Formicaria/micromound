using System.Runtime.InteropServices;

namespace Micromound.Drivers;

/// <summary>
/// The Linux system calls a character-device driver needs — open, ioctl with a buffer, write, read,
/// close — behind an interface, so a driver's encoding and error handling can be exercised against a
/// fake kernel that decodes what it was handed, with no device node. Both real ports use it: the GPIO
/// character device (<see cref="GpioChardevOutput"/>) and the I2C bus (<see cref="LinuxI2cBus"/>). On
/// a device it is <see cref="LibcIo"/>.
/// </summary>
public interface ILinuxIo
{
    /// <summary>open(2). Returns the descriptor, or -1 with <see cref="LastErrno"/> set.</summary>
    int Open(string path, int flags);

    /// <summary>ioctl(2) with a pointer argument. The kernel may write back into <paramref name="buffer"/>. Returns -1 on failure.</summary>
    int Ioctl(int fd, uint request, byte[] buffer);

    /// <summary>ioctl(2) with an integer argument (<c>I2C_SLAVE</c> takes the address as a value, not a pointer). Returns -1 on failure.</summary>
    int Ioctl(int fd, uint request, ulong argument);

    /// <summary>write(2). Returns the bytes written, or -1.</summary>
    nint Write(int fd, byte[] buffer, int count);

    /// <summary>read(2) into <paramref name="buffer"/>. Returns the bytes read, or -1.</summary>
    nint Read(int fd, byte[] buffer, int count);

    /// <summary>close(2).</summary>
    int Close(int fd);

    /// <summary>The errno of the last failed call.</summary>
    int LastErrno();
}

/// <summary>libc, via P/Invoke. No <c>unsafe</c>: buffers are pinned by the marshaller for the call.</summary>
public sealed class LibcIo : ILinuxIo
{
    public static readonly LibcIo Instance = new();

    public const int O_RDWR = 2;
    public const int O_CLOEXEC = 0x80000;

    public int Open(string path, int flags) => open(path, flags);
    public int Ioctl(int fd, uint request, byte[] buffer) => ioctl(fd, request, buffer);
    public int Ioctl(int fd, uint request, ulong argument) => ioctl(fd, request, (nuint)argument);
    public nint Write(int fd, byte[] buffer, int count) => write(fd, buffer, (nuint)count);
    public nint Read(int fd, byte[] buffer, int count) => read(fd, buffer, (nuint)count);
    public int Close(int fd) => close(fd);
    public int LastErrno() => Marshal.GetLastWin32Error();

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, [In, Out] byte[] arg);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, nuint arg);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint write(int fd, byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint read(int fd, [Out] byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int close(int fd);
}
