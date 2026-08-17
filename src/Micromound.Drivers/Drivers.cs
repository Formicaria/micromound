using Micromound.Capabilities;
using Micromound.Protocol;

namespace Micromound.Drivers;

/// <summary>Physical interfaces a driver may sit on — ARCHITECTURE.md Layer 4.</summary>
public static class BusKinds
{
    public const string Gpio = "gpio";
    public const string I2c = "i2c";
    public const string Spi = "spi";
    public const string Uart = "uart";
    public const string Pwm = "pwm";
    public const string Can = "can";
    public const string Usb = "usb";
    public const string Serial = "serial";
    public const string Ble = "ble";
    public const string Camera = "camera";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Gpio, I2c, Spi, Uart, Pwm, Can, Usb, Serial, Ble, Camera
    };
}

/// <summary>Driver health, reported upward as a fact — never repaired automatically.</summary>
public enum DriverHealth
{
    Healthy,
    Degraded,
    Faulted,
    Absent
}

/// <summary>
/// A hardware adapter — ARCHITECTURE.md Layer 4. Drivers are boring by design: deterministic,
/// unit-testable through a bus abstraction, and independent of any reasoning provider.
///
/// A driver's job is to turn one semantic capability into one device operation and report what
/// happened. It does not decide whether the operation was allowed; by the time it is called that
/// question is already settled, and it has no access to the charter that settled it.
/// </summary>
public interface IDriver
{
    /// <summary>Driver id as it appears in a manifest's hardware binding, e.g. "gpio_relay".</summary>
    string DriverId { get; }

    /// <summary>Which bus this driver sits on — see <see cref="BusKinds"/>.</summary>
    string Bus { get; }

    DriverHealth Health { get; }

    /// <summary>
    /// Validate this driver's slice of the manifest — a pin number, a bus address, a channel.
    /// Settings arrive as strings and are parsed here, where the knowledge of what is legal
    /// lives. Invalid settings fail closed: the driver does not initialize.
    /// </summary>
    ValidationResult Configure(IReadOnlyDictionary<string, string> settings);

    /// <summary>
    /// The capabilities this driver exposes once configured, with the hardware limits and
    /// parameter ranges the device physically imposes. These become the innermost limit tier.
    /// </summary>
    IReadOnlyList<CapabilityDescriptor> Capabilities { get; }

    /// <summary>Executors the kernel binds. This is the only path from software to hardware.</summary>
    IReadOnlyList<ICapabilityExecutor> Executors { get; }

    /// <summary>Put the hardware into its declared passive state. Called on stop, quiesce, and shutdown.</summary>
    void EnterSafeState();
}

/// <summary>
/// Drivers available in this build, resolved by the id a manifest names. Populated at startup;
/// a manifest naming a driver that is not here fails validation rather than being ignored.
/// </summary>
public sealed class DriverRegistry
{
    private readonly Dictionary<string, IDriver> _drivers = new(StringComparer.Ordinal);

    public IReadOnlySet<string> AvailableDriverIds() =>
        new HashSet<string>(_drivers.Keys, StringComparer.Ordinal);

    public void Register(IDriver driver) => _drivers[driver.DriverId] = driver;

    public bool TryGet(string driverId, out IDriver driver) => _drivers.TryGetValue(driverId, out driver!);

    public IEnumerable<IDriver> All => _drivers.Values;

    /// <summary>Every driver into its safe state, on stop or shutdown. Best effort, never skipped.</summary>
    public void EnterSafeState()
    {
        foreach (var driver in _drivers.Values)
        {
            try { driver.EnterSafeState(); }
            catch { /* a driver that cannot be quiesced must not stop the others being quiesced */ }
        }
    }
}
