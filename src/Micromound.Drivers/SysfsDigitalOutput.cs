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
/// <para><b>On-device note:</b> sysfs GPIO is deprecated in favour of the libgpiod character device,
/// and the kernel creates a pin's directory asynchronously after <c>export</c>. This implementation
/// keeps the file protocol simple and testable; a libgpiod (chardev) backing and export-settle
/// retries are a follow-up, and the value writes here must be verified on real hardware.</para>
/// </summary>
public sealed class SysfsDigitalOutput : IDigitalOutput, IDisposable
{
    private readonly int _pin;
    private readonly string _root;
    private readonly string _pinDirectory;
    private bool _released;

    public SysfsDigitalOutput(int pin, string sysfsRoot = "/sys/class/gpio")
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
            TryWrite(Path.Combine(_root, "export"), pin.ToString());

        // Drive it low as its initial safe level, then declare it an output.
        Write(Path.Combine(_pinDirectory, "direction"), "out");
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
