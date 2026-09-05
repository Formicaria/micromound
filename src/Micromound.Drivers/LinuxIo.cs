using System.Runtime.InteropServices;

namespace Micromound.Drivers;

/// <summary>
/// The three Linux system calls a character-device driver needs — open, ioctl with a buffer, close —
/// behind an interface, so a driver's request-struct encoding can be exercised against a fake that
/// decodes what it was handed, with no device node. On a device it is <see cref="LibcIo"/>.
/// </summary>
public interface ILinuxIo
{
    /// <summary>open(2). Returns the descriptor, or -1 with <see cref="LastErrno"/> set.</summary>
    int Open(string path, int flags);

    /// <summary>ioctl(2) with a pointer argument. The kernel may write back into <paramref name="buffer"/>. Returns -1 on failure.</summary>
    int Ioctl(int fd, uint request, byte[] buffer);

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
    public int Close(int fd) => close(fd);
    public int LastErrno() => Marshal.GetLastWin32Error();

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, [In, Out] byte[] arg);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int close(int fd);
}
