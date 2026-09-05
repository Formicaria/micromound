using Micromound.Protocol;

namespace Micromound.Drivers;

/// <summary>
/// Creates a fresh, unconfigured driver for the driver-type name a manifest's hardware binding
/// names (<see cref="HardwareBinding.Driver"/>). The two are separate steps on purpose: a manifest
/// can bind the same driver type to several devices with different settings, so each binding gets
/// its own instance from <see cref="Create"/> and is then handed its own slice of the manifest
/// through <see cref="IDriver.Configure"/>, which fails closed on anything it cannot parse.
/// </summary>
public interface IDriverFactory
{
    /// <summary>The driver-type name a manifest binds, e.g. "digital_actuator".</summary>
    string DriverType { get; }

    /// <summary>A fresh, not-yet-configured driver instance.</summary>
    IDriver Create();

    /// <summary>
    /// What this driver type is and which settings it reads, for a controller building a hardware
    /// form (<see cref="DriverSchemaCatalog"/>). Descriptive only — the driver still validates every
    /// setting itself and fails closed.
    /// </summary>
    DriverTypeSchema Schema { get; }
}

/// <summary>
/// The driver types this build can instantiate, resolved by the name a manifest binds. A manifest
/// naming a type that is not registered here fails composition rather than being silently skipped —
/// the same fail-closed rule <see cref="DriverRegistry"/> applies to already-built driver instances,
/// one step earlier, at the point a device is turned into a driver at all.
/// </summary>
public sealed class DriverFactoryRegistry
{
    private readonly Dictionary<string, IDriverFactory> _factories = new(StringComparer.Ordinal);

    public void Register(IDriverFactory factory) => _factories[factory.DriverType] = factory;

    public IReadOnlySet<string> AvailableDriverTypes() =>
        new HashSet<string>(_factories.Keys, StringComparer.Ordinal);

    public bool TryGet(string driverType, out IDriverFactory factory) =>
        _factories.TryGetValue(driverType, out factory!);

    /// <summary>The schema of every driver type registered here, in driver-type order — what a device
    /// sends at enrollment so its controller can describe exactly the hardware THIS build supports.</summary>
    public IReadOnlyList<DriverTypeSchema> Describe() =>
        _factories.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Value.Schema).ToList();
}

/// <summary>The configured drivers a manifest resolved to, or the errors that stopped it.</summary>
public sealed record DriverResolution(IReadOnlyList<IDriver> Drivers, ValidationResult Result)
{
    public bool IsValid => Result.IsValid;
}

/// <summary>
/// Turns a manifest's hardware section into configured drivers — the step the M4 host runs between
/// "a manifest arrived" and "the kernel has executors to bind". It is deterministic and fails
/// closed as a whole: if any device names an unknown driver, fails to configure, or would collide
/// with another device's driver identity, NO drivers are returned and every reason is reported, so
/// a mound never comes up half-wired with some hardware silently missing.
/// </summary>
public static class ManifestDriverComposer
{
    public static DriverResolution Compose(MoundManifest manifest, DriverFactoryRegistry factories)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(factories);

        var errors = new List<string>();
        var drivers = new List<IDriver>();
        var seenDriverIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (device, binding) in manifest.Hardware)
        {
            if (!factories.TryGet(binding.Driver, out var factory))
            {
                errors.Add($"device '{device}': no driver '{binding.Driver}' in this build");
                continue;
            }

            var driver = factory.Create();
            var configured = driver.Configure(binding.Settings);
            if (!configured.IsValid)
            {
                foreach (var error in configured.Errors)
                    errors.Add($"device '{device}': {error}");
                continue;
            }

            // Two devices resolving to the same driver identity would collide in the kernel's
            // DriverRegistry, one silently overwriting the other. That is a configuration error,
            // caught here where it can name both the device and the identity.
            if (!seenDriverIds.Add(driver.DriverId))
            {
                errors.Add($"device '{device}': driver identity '{driver.DriverId}' is already " +
                           "used by another device — give each a distinct capability");
                continue;
            }

            drivers.Add(driver);
        }

        return errors.Count > 0
            ? new DriverResolution([], new ValidationResult(errors))
            : new DriverResolution(drivers, new ValidationResult([]));
    }
}
