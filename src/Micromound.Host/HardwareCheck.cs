using Micromound.Capabilities;
using Micromound.Drivers;
using Micromound.Protocol;

namespace Micromound.Host;

/// <summary>
/// "Does my wiring match my manifest?" — the question an operator standing at a board asks before
/// anything is chartered. <see cref="Run"/> opens every device the manifest binds through the SAME
/// factories the daemon would use, exactly as bring-up would (fail-closed, port opened last, at the
/// safe level), takes ONE reading from each sensor, and reports per device: claimed or refused, and
/// why. It never actuates anything: an output line is requested at its safe level and left there,
/// and no mound is composed, so there is no authority, no mission, no evidence — just the ports.
///
/// <para>This is the daemon's <c>--check-hardware</c>. It exists because the alternative on a real
/// board is "start the daemon, read the refusal in the log, edit the manifest, repeat", and because
/// the first reading from a probe is the moment you find out the channel number was wrong. The
/// report is the daemon's own view; it grants nothing and proves nothing to the controller.</para>
/// </summary>
public static class HardwareCheck
{
    /// <summary>One device's result.</summary>
    /// <param name="Device">The manifest's device name.</param>
    /// <param name="Driver">Its driver type.</param>
    /// <param name="Ok">True when the port was claimed (and, for a sensor, read).</param>
    /// <param name="Capability">The capability the driver exposes, when configured.</param>
    /// <param name="Detail">What happened, in words: the refusal reasons, or what was observed.</param>
    /// <param name="Reading">A sensor's first value in its configured unit, when one was taken.</param>
    /// <param name="Unit">The unit of that reading, from the manifest.</param>
    public sealed record DeviceReport(string Device, string Driver, bool Ok, string Capability, string Detail, double? Reading, string Unit);

    /// <summary>The whole check: every device, and whether all of them passed.</summary>
    public sealed record Report(IReadOnlyList<DeviceReport> Devices)
    {
        public bool AllOk => Devices.All(d => d.Ok);
    }

    public static Report Run(MoundManifest manifest, DriverFactoryRegistry factories, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(factories);

        var reports = new List<DeviceReport>();
        foreach (var (device, binding) in manifest.Hardware)
        {
            var settings = binding.Settings ?? new Dictionary<string, string>(StringComparer.Ordinal);
            settings.TryGetValue("capability", out var declared);
            declared ??= "";

            if (!factories.TryGet(binding.Driver, out var factory))
            {
                reports.Add(new DeviceReport(device, binding.Driver, false, declared,
                    $"no driver '{binding.Driver}' in this build (have: {string.Join(", ", factories.AvailableDriverTypes().Order(StringComparer.Ordinal))})", null, ""));
                continue;
            }

            IDriver driver;
            try
            {
                driver = factory.Create();
            }
            catch (Exception ex)
            {
                reports.Add(new DeviceReport(device, binding.Driver, false, declared, "driver could not be created: " + ex.Message, null, ""));
                continue;
            }

            var configured = driver.Configure(settings);
            if (!configured.IsValid)
            {
                reports.Add(new DeviceReport(device, binding.Driver, false, declared, "refused: " + string.Join("; ", configured.Errors), null, ""));
                continue;
            }

            var capability = driver.Capabilities.Count > 0 ? driver.Capabilities[0].Id : declared;
            settings.TryGetValue("unit", out var unit);
            unit ??= "";

            // A sensor is read once — the one thing a check can observe without changing the world.
            // An actuator is only claimed: its line is at the safe level and stays there.
            var executor = driver.Executors.FirstOrDefault(e => e.CapabilityId == capability);
            var isSensor = driver.Capabilities.Count > 0 && driver.Capabilities[0].Class == ActionClass.Observe;
            if (isSensor && executor is not null)
            {
                var outcome = executor.Execute(new CapabilityExecution
                {
                    CapabilityId = capability,
                    Parameters = new Dictionary<string, double>(),
                    StartedAt = now,
                    EffectiveLimits = new CapabilityLimits()
                });
                if (!outcome.Succeeded)
                {
                    reports.Add(new DeviceReport(device, binding.Driver, false, capability, "claimed, but the first read failed: " + outcome.Detail, null, unit));
                    continue;
                }
                double? value = null;
                if (outcome.Evidence.Count > 0 && EvidenceReadings.TryRead(outcome.Evidence[0], out var v))
                    value = v;
                reports.Add(new DeviceReport(device, binding.Driver, true, capability,
                    value is { } r ? $"claimed; first reading {r:0.####}{(unit.Length > 0 ? " " + unit : "")}" : "claimed; read once, no numeric reading", value, unit));
            }
            else
            {
                var line = driver is DigitalActuatorDriver ? "output line claimed and held at its SAFE level (not actuated)" : "claimed";
                reports.Add(new DeviceReport(device, binding.Driver, true, capability, line, null, unit));
            }

            // Leave nothing energized behind the check; the process exit releases the ports.
            try { driver.EnterSafeState(); } catch { /* reported already via the port; a check never throws */ }
        }
        return new Report(reports);
    }

    /// <summary>The report as the daemon prints it: one line per device, then a verdict.</summary>
    public static string Format(Report report, string backing)
    {
        var lines = new List<string> { $"micromound: hardware check ({backing})" };
        foreach (var d in report.Devices)
            lines.Add($"  {(d.Ok ? "OK  " : "FAIL")}  {d.Device,-16} {d.Driver,-18} {d.Capability,-24} {d.Detail}");
        lines.Add(report.Devices.Count == 0
            ? "  (the manifest binds no hardware)"
            : report.AllOk
                ? $"  all {report.Devices.Count} device(s) claimed. Nothing was actuated; no mound was composed."
                : $"  {report.Devices.Count(d => !d.Ok)} of {report.Devices.Count} device(s) refused — the daemon would refuse bring-up with this manifest.");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The names of devices whose settings name PHYSICAL ports (a pin, a bus, a channel) — the ones
    /// that an in-memory run would silently fake. The daemon refuses to run such a manifest in-memory
    /// unless told, in so many words, to simulate.
    /// </summary>
    public static IReadOnlyList<string> DevicesNamingPhysicalPorts(MoundManifest manifest) =>
        manifest.Hardware
            .Where(h => h.Value.Settings.Keys.Any(k => k is "pin" or "chip" or "channel" or "bus" or "address"))
            .Select(h => h.Key)
            .ToList();
}
