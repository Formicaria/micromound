using Micromound.Drivers;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The Linux I2C bus (<see cref="LinuxI2cBus"/>, i2c-dev) over the system-call seam: the open /
/// select-slave / transfer sequence and every error path, against a fake kernel. Since v0.9.17 the bus
/// shares <see cref="ILinuxIo"/> with the GPIO character device, which is what makes these tests
/// possible — before, its libc calls were unreachable from a test. The one thing that only a board can
/// prove is that the electrical transfers happen; everything about how they are asked for is here.
/// </summary>
public sealed class LinuxI2cBusTests
{
    /// <summary>A kernel with one I2C bus node and one chip at 0x48 that echoes a register file.</summary>
    private sealed class FakeKernel : ILinuxIo
    {
        public List<(string Path, int Flags)> Opens { get; } = [];
        public List<(int Fd, uint Request, ulong Argument)> ValueIoctls { get; } = [];
        public List<byte[]> Writes { get; } = [];
        public List<int> Closed { get; } = [];
        public HashSet<string> Present { get; } = ["/dev/i2c-1"];
        public HashSet<ulong> ChipsAt { get; } = [0x48];
        public byte[] NextRead { get; set; } = [0x85, 0x83];
        public bool ShortRead { get; set; }
        public bool FailWrite { get; set; }
        public int Errno { get; private set; }
        private int _fd = 30;
        private readonly HashSet<int> _open = [];

        public int Open(string path, int flags)
        {
            Opens.Add((path, flags));
            if (!Present.Contains(path)) { Errno = 2; return -1; }
            var fd = _fd++; _open.Add(fd); return fd;
        }
        public int Ioctl(int fd, uint request, byte[] buffer) { Errno = 25; return -1; }   // i2c-dev's I2C_SLAVE is a value ioctl
        public int Ioctl(int fd, uint request, ulong argument)
        {
            ValueIoctls.Add((fd, request, argument));
            if (!_open.Contains(fd)) { Errno = 9; return -1; }
            if (request != LinuxI2cBus.I2cSlaveIoctl) { Errno = 25; return -1; }
            if (!ChipsAt.Contains(argument)) { Errno = 16; return -1; }   // EBUSY in practice means "claimed by a kernel driver"; ENXIO/EREMOTEIO come later on transfer — either way, refused
            return 0;
        }
        public nint Write(int fd, byte[] buffer, int count)
        {
            if (!_open.Contains(fd)) { Errno = 9; return -1; }
            if (FailWrite) { Errno = 121; return -1; }   // EREMOTEIO: no acknowledge
            Writes.Add(buffer.Take(count).ToArray()); return count;
        }
        public nint Read(int fd, byte[] buffer, int count)
        {
            if (!_open.Contains(fd)) { Errno = 9; return -1; }
            var n = ShortRead ? Math.Max(0, count - 1) : Math.Min(count, NextRead.Length);
            Array.Copy(NextRead, buffer, n);
            return n;
        }
        public int Close(int fd) { Closed.Add(fd); _open.Remove(fd); return 0; }
        public int LastErrno() => Errno;
        public int OpenCount => _open.Count;
    }

    [Fact]
    public void Opening_selects_the_slave_by_value_ioctl_on_the_bus_node()
    {
        var k = new FakeKernel();
        using var bus = new LinuxI2cBus(1, 0x48, k);

        var open = Assert.Single(k.Opens);
        Assert.Equal("/dev/i2c-1", open.Path);
        Assert.Equal(LibcIo.O_RDWR | LibcIo.O_CLOEXEC, open.Flags);
        var sel = Assert.Single(k.ValueIoctls);
        Assert.Equal(0x0703u, sel.Request);            // I2C_SLAVE
        Assert.Equal(0x48UL, sel.Argument);            // the address as the ARGUMENT, not a pointer
        Assert.Equal(1, k.OpenCount);
    }

    [Fact]
    public void Writes_and_reads_go_to_the_selected_descriptor_whole()
    {
        var k = new FakeKernel { NextRead = [0xC3, 0x83] };
        using var bus = new LinuxI2cBus(1, 0x48, k);

        bus.Write([0x01, 0xC3, 0x83]);
        Span<byte> back = stackalloc byte[2];
        bus.Read(back);

        Assert.Equal(new byte[] { 0x01, 0xC3, 0x83 }, Assert.Single(k.Writes));
        Assert.Equal(0xC3, back[0]);
        Assert.Equal(0x83, back[1]);
    }

    [Fact]
    public void A_missing_bus_node_is_an_io_error_naming_the_node_and_the_fix()
    {
        var k = new FakeKernel();
        k.Present.Clear();
        var ex = Assert.Throws<IOException>(() => new LinuxI2cBus(1, 0x48, k));
        Assert.Contains("/dev/i2c-1", ex.Message);
        Assert.Contains("errno 2", ex.Message);
        Assert.Contains("i2c group", ex.Message);
    }

    [Fact]
    public void An_address_the_kernel_refuses_closes_the_node_and_throws()
    {
        var k = new FakeKernel();
        var ex = Assert.Throws<IOException>(() => new LinuxI2cBus(1, 0x49, k));
        Assert.Contains("0x49", ex.Message);
        Assert.Equal(0, k.OpenCount);   // the descriptor opened for the failed select was closed
        Assert.Single(k.Closed);
    }

    [Fact]
    public void A_short_read_is_an_error_that_says_how_short()
    {
        var k = new FakeKernel { ShortRead = true };
        using var bus = new LinuxI2cBus(1, 0x48, k);
        var ex = Assert.Throws<IOException>(() => { var b = new byte[2]; bus.Read(b); });
        Assert.Contains("returned 1 of 2 bytes", ex.Message);
    }

    [Fact]
    public void A_write_the_device_does_not_acknowledge_is_an_error_with_the_errno()
    {
        var k = new FakeKernel { FailWrite = true };
        using var bus = new LinuxI2cBus(1, 0x48, k);
        var ex = Assert.Throws<IOException>(() => bus.Write([0x01]));
        Assert.Contains("errno 121", ex.Message);
        Assert.Contains("no acknowledge", ex.Message);
    }

    [Fact]
    public void Disposing_closes_the_node_once_and_a_later_transfer_is_refused()
    {
        var k = new FakeKernel();
        var bus = new LinuxI2cBus(1, 0x48, k);
        bus.Dispose();
        bus.Dispose();
        Assert.Single(k.Closed);
        Assert.Throws<ObjectDisposedException>(() => bus.Write([0x00]));
    }

    [Fact]
    public void The_ads1115_over_the_real_bus_class_issues_the_datasheet_transfers()
    {
        // The chip driver over the REAL bus class over the fake kernel: probe (pointer write + read),
        // then a read: config write, config poll, conversion pointer + read.
        var k = new FakeKernel { NextRead = [0x85, 0x83] };
        var bus = new LinuxI2cBus(1, 0x48, k);
        var input = new Ads1115AnalogInput(bus, channel: 0);
        k.Writes.Clear();
        k.NextRead = [0xC3, 0x83];   // config reads back with OS set; conversion read gets these too (0xC383 → negative raw; fine for the transfer check)

        input.Read();

        Assert.Equal(new byte[] { 0x01, 0xC3, 0x83 }, k.Writes[0]);   // start conversion: Config = 0xC383
        Assert.Equal(new byte[] { 0x01 }, k.Writes[1]);               // poll Config
        Assert.Equal(new byte[] { 0x00 }, k.Writes[^1]);              // read Conversion
    }

    [Fact]
    public void Sysfs_export_that_never_creates_the_pin_directory_is_refused_with_a_reason()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-sysfs-settle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ex = Assert.Throws<IOException>(() => new SysfsDigitalOutput(23, root));
            Assert.Contains("did not appear", ex.Message);
            Assert.Equal("23", File.ReadAllText(Path.Combine(root, "export")));   // the export was attempted
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Sysfs_export_that_settles_late_is_waited_for()
    {
        var root = Path.Combine(Path.GetTempPath(), "mm-sysfs-late-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // The "kernel": create the pin directory 50 ms after export is written.
            using var watcher = Start(new Thread(() =>
            {
                var export = Path.Combine(root, "export");
                while (!File.Exists(export)) Thread.Sleep(2);
                Thread.Sleep(50);
                Directory.CreateDirectory(Path.Combine(root, "gpio24"));
            }));

            using var port = new SysfsDigitalOutput(24, root, initialHigh: true);
            Assert.Equal("high", File.ReadAllText(Path.Combine(root, "gpio24", "direction")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static IDisposable Start(Thread t) { t.IsBackground = true; t.Start(); return new Joiner(t); }
    private sealed class Joiner(Thread t) : IDisposable { public void Dispose() => t.Join(1000); }
}
