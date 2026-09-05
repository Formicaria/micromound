namespace Micromound.Drivers;

/// <summary>
/// A real Linux GPIO output line over the sysfs interface (<c>/sys/class/gpio</c>) — the first
/// hardware backing for <see cref="IDigitalOutput"/>, the seam the generic digital actuator drives.
/// A pin is a set of files: write the number to <c>export</c> to claim it, write <c>out</c> to its
/// <c>direction</c>, then write <c>1</c>/<c>0</c> to its <c>value</c>. <see cref="Dispose"/> writes
/// the number to <c>unexport</c> to release it.
///
/// <para>The root path is injectable so the file protocol can be exercised against a fake tree with
/// no hardware; on a device it is <c>/sys/class/gpio</c>. Export is tolerant: a pin already claimed
/// (a prior run that did not release it) is not an error, it is re-used. This port is polarity-
/// agnostic — it writes the logical level it is given; the driver above it owns active-high/low.</para>
///
/// <para><b>Initial level is set atomically.</b> sysfs accepts <c>high</c>/<c>low</c> as a direction,
/// which makes the pin an output already at that level; writing <c>out</c> (low) and then the safe
/// level would energize an active-low load for the instant in between. The factory passes the safe
/// level (<c>!active_high</c>) as <paramref name="initialHigh"/>.</para>
///
/// <para><b>On-device note:</b> sysfs GPIO is deprecated in favour of the GPIO character device —
/// <see cref="GpioChardevOutput"/> is the preferred backing (daemon <c>--gpio chardev</c>, the default);
/// this one remains for kernels built with <c>CONFIG_GPIO_SYSFS</c>. The kernel creates a pin's
/// directory asynchronously after <c>export</c>, so the constructor waits briefly for it (up to
/// 200 ms) and refuses with a reason if it never appears. The value writes here must be verified on
/// real hardware.</para>
/// </summary>
public sealed class SysfsDigitalOutput : IDigitalOutput, IDisposable
{
    /// <summary>How long to wait for the kernel to create <c>gpioN/</c> after <c>export</c>: 20 × 10 ms.</summary>
    public const int ExportSettlePolls = 20;
    public const int ExportSettlePollMs = 10;

    private readonly int _pin;
    private readonly string _root;
    private readonly string _pinDirectory;
    private bool _released;

    /// <param name="pin">The global sysfs GPIO number (a BCM number on a Raspberry Pi with chip base 0).</param>
    /// <param name="sysfsRoot">Injectable for tests; <c>/sys/class/gpio</c> on a device.</param>
    /// <param name="initialHigh">The level the pin is driven to as it becomes an output — the SAFE level.</param>
    public SysfsDigitalOutput(int pin, string sysfsRoot = "/sys/class/gpio", bool initialHigh = false)
    {
        if (pin < 0)
            throw new ArgumentOutOfRangeException(nameof(pin), pin, "a GPIO pin number cannot be negative");
        ArgumentException.ThrowIfNullOrWhiteSpace(sysfsRoot);

        _pin = pin;
        _root = sysfsRoot;
        _pinDirectory = Path.Combine(_root, "gpio" + pin);

        // Claim the pin. If its directory already exists it is already exported (a prior run that
        // did not release it, or a shared line) — re-use it rather than failing.
        if (!Directory.Exists(_pinDirectory))
        {
            TryWrite(Path.Combine(_root, "export"), pin.ToString());
            // The kernel creates gpioN/ ASYNCHRONOUSLY after export (udev then fixes its ownership).
            // Wait briefly for it; a pin that never appears is a refusal with a reason, not a
            // DirectoryNotFoundException from the next write.
            for (var i = 0; i < ExportSettlePolls && !Directory.Exists(_pinDirectory); i++)
                Thread.Sleep(ExportSettlePollMs);
            if (!Directory.Exists(_pinDirectory))
                throw new IOException($"GPIO {pin} did not appear under {_root} within {ExportSettlePolls * ExportSettlePollMs} ms of export; is this a valid pin on this board, and is sysfs GPIO enabled?");
        }

        // Declare it an output ALREADY AT its initial (safe) level: "high"/"low" set direction and
        // value in one write, so the pin never sits at the wrong level between the two.
        Write(Path.Combine(_pinDirectory, "direction"), initialHigh ? "high" : "low");
        State = initialHigh;
    }

    public bool State { get; private set; }

    public void Write(bool high)
    {
        Write(Path.Combine(_pinDirectory, "value"), high ? "1" : "0");
        State = high;
    }

    public void Dispose()
    {
        if (_released)
            return;
        _released = true;
        // Release the pin. Best-effort: a device being torn down must not throw on a busy sysfs node.
        TryWrite(Path.Combine(_root, "unexport"), _pin.ToString());
    }

    /// <summary>Write that must succeed — a failure here means the line is not controllable, and the
    /// driver's <see cref="GenericDriverBase.Configure"/> turns that into a fail-closed refusal.</summary>
    private static void Write(string path, string value) => File.WriteAllText(path, value);

    /// <summary>Best-effort write for export/unexport, where "already done" is not a failure.</summary>
    private static void TryWrite(string path, string value)
    {
        try { File.WriteAllText(path, value); }
        catch (IOException) { /* already exported / released, or a benign sysfs EBUSY */ }
    }
}
