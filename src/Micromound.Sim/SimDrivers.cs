using System.Globalization;
using Micromound.Capabilities;
using Micromound.Drivers;
using Micromound.Protocol;

namespace Micromound.Sim;

/// <summary>
/// Fake hardware behind the real driver seam.
///
/// These implement <see cref="IDriver"/> — the same interface a BME280 or a GPIO relay driver
/// will implement in M4 — rather than plugging executors straight into the kernel, because the
/// simulator's job is to prove the composition a Pi will actually run:
/// driver → capability → kernel → ant → coordinator. A sim that shortcuts the driver layer would
/// leave that layer's contract (configure fails closed, health is reported not repaired, safe
/// state is a driver duty) exercised by nothing until real hardware arrives to find the bugs.
/// </summary>
public abstract class SimDriverBase : IDriver, IEvidenceSource
{
    private readonly List<string> _configurationErrors = [];

    public abstract string DriverId { get; }

    public abstract string Bus { get; }

    public DriverHealth Health { get; private set; } = DriverHealth.Healthy;

    /// <summary>Why the driver is faulted, for the record that refuses the work.</summary>
    public string FaultDetail { get; private set; } = "";

    /// <summary>How many times something told this driver to enter its passive state.</summary>
    public int SafeStateEntries { get; private set; }

    /// <summary>
    /// Flip off to simulate dead instrumentation: the work still happens, nothing observes it,
    /// and the kernel's evidence gate demotes the outcome. Commands are not evidence.
    /// </summary>
    public bool ProduceEvidence { get; set; } = true;

    /// <summary>Receives every evidence item this driver's hardware observes. Set by composition.</summary>
    public Action<EvidenceItem>? Publish { get; set; }

    public abstract IReadOnlyList<CapabilityDescriptor> Capabilities { get; }

    public abstract IReadOnlyList<ICapabilityExecutor> Executors { get; }

    /// <summary>Simulate a hardware fault. Reported upward as a fact; nothing here repairs it.</summary>
    public void Fault(string detail)
    {
        Health = DriverHealth.Faulted;
        FaultDetail = detail;
    }

    public virtual ValidationResult Configure(IReadOnlyDictionary<string, string> settings)
    {
        _configurationErrors.Clear();
        OnConfigure(settings, _configurationErrors);

        if (_configurationErrors.Count > 0) Health = DriverHealth.Absent;   // fail closed: never initialized
        return new ValidationResult([.. _configurationErrors]);
    }

    protected virtual void OnConfigure(IReadOnlyDictionary<string, string> settings, List<string> errors) { }

    public virtual void EnterSafeState() => SafeStateEntries++;

    protected static bool TryParseDouble(string value, out double parsed) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
}

/// <summary>
/// A simulated sensor: one <c>sense.</c> capability whose reading a test or a harness sets.
/// The reading travels as an <see cref="EvidenceReadings"/> payload, so a mission condition can
/// compare it and a Witness can confirm against it — the same bytes a real ADS1115 driver will
/// produce.
/// </summary>
public sealed class SimSensorDriver : SimDriverBase
{
    private readonly SensorExecutor _executor;

    public SimSensorDriver(string capability, double reading = 0, string unit = "")
    {
        Capability = capability;
        Reading = reading;
        Unit = unit;
        _executor = new SensorExecutor(this);

        Descriptor = new CapabilityDescriptor
        {
            Id = capability,
            Class = ActionClass.Observe,
            Description = "simulated sensor " + capability
        };
    }

    public override string DriverId => "sim_sensor:" + Capability;

    public override string Bus => BusKinds.I2c;

    public string Capability { get; }

    /// <summary>The physical quantity the fake world currently holds. Tests move it.</summary>
    public double Reading { get; set; }

    public string Unit { get; }

    public CapabilityDescriptor Descriptor { get; }

    public override IReadOnlyList<CapabilityDescriptor> Capabilities => [Descriptor];

    public override IReadOnlyList<ICapabilityExecutor> Executors => [_executor];

    protected override void OnConfigure(IReadOnlyDictionary<string, string> settings, List<string> errors)
    {
        if (settings.TryGetValue("reading", out var raw))
        {
            if (TryParseDouble(raw, out var parsed)) Reading = parsed;
            else errors.Add($"sim_sensor '{Capability}': setting 'reading' is not a number: '{raw}'");
        }
    }

    private sealed class SensorExecutor(SimSensorDriver driver) : ICapabilityExecutor
    {
        public string CapabilityId => driver.Capability;

        public bool IsAvailable => driver.Health == DriverHealth.Healthy;

        public ExecutionOutcome Execute(CapabilityExecution execution)
        {
            if (!driver.ProduceEvidence) return ExecutionOutcome.Ok();

            var item = EvidenceReadings.Create(
                Guid.NewGuid().ToString(), driver.Capability, driver.Reading, execution.StartedAt,
                unit: driver.Unit, source: driver.DriverId);

            driver.Publish?.Invoke(item);
            return ExecutionOutcome.Ok([item]);
        }
    }
}

/// <summary>
/// A simulated relay: one <c>act.</c> capability with the hardware limits compiled into the
/// driver, exactly where a real GPIO relay driver declares what its hardware tolerates. The
/// contact sensor that observes the switch is part of the same device, so its confirmation rides
/// back on the execution — and turning <see cref="SimDriverBase.ProduceEvidence"/> off models the
/// contact sensor dying while the relay keeps switching.
/// </summary>
public sealed class SimRelayDriver : SimDriverBase
{
    private readonly RelayExecutor _executor;

    public SimRelayDriver(string capability, CapabilityLimits? hardwareLimits = null)
    {
        Capability = capability;
        _executor = new RelayExecutor(this);

        Descriptor = new CapabilityDescriptor
        {
            Id = capability,
            Class = ActionClass.Benign,
            Description = "simulated relay " + capability,
            HardwareLimits = hardwareLimits ?? new CapabilityLimits
            {
                MaxOnSeconds = 30, MinOffSeconds = 300, MaxRatePerHour = 6
            },
            Parameters = new HashSet<string>(StringComparer.Ordinal) { "on_s" },
            DurationParameter = "on_s"
        };
    }

    public override string DriverId => "sim_relay:" + Capability;

    public override string Bus => BusKinds.Gpio;

    public string Capability { get; }

    public CapabilityDescriptor Descriptor { get; }

    /// <summary>How many times the relay actually switched — the fake world's ground truth.</summary>
    public int Actuations { get; private set; }

    /// <summary>Effective seconds of the last actuation, after every limit tier.</summary>
    public double LastOnSeconds { get; private set; }

    /// <summary>
    /// The fake physics hook: called with the effective duration whenever the relay actually
    /// runs, so a harness can make watering raise soil moisture. Deterministic and injected —
    /// the driver itself knows nothing about what the relay is plumbed to.
    /// </summary>
    public Action<double>? OnActuated { get; set; }

    public override IReadOnlyList<CapabilityDescriptor> Capabilities => [Descriptor];

    public override IReadOnlyList<ICapabilityExecutor> Executors => [_executor];

    public override void EnterSafeState()
    {
        base.EnterSafeState();
        // The relay itself is momentary in this simulation; entering the safe state is recorded
        // rather than visible, which is exactly what M4's real driver must improve on.
    }

    private sealed class RelayExecutor(SimRelayDriver driver) : ICapabilityExecutor
    {
        public string CapabilityId => driver.Capability;

        public bool IsAvailable => driver.Health == DriverHealth.Healthy;

        public ExecutionOutcome Execute(CapabilityExecution execution)
        {
            var onSeconds = execution.Parameters.TryGetValue("on_s", out var value) ? value : 0;

            // The physical work happens whether or not anything observes it — that ordering is
            // the entire point of the dead-contact-sensor scenario.
            driver.Actuations++;
            driver.LastOnSeconds = onSeconds;
            driver.OnActuated?.Invoke(onSeconds);

            if (!driver.ProduceEvidence) return ExecutionOutcome.Ok();

            var item = EvidenceReadings.Create(
                Guid.NewGuid().ToString(), driver.Capability, 1, execution.StartedAt,
                unit: "contact", source: driver.DriverId + ".contact");

            driver.Publish?.Invoke(item);
            return ExecutionOutcome.Ok([item]);
        }
    }
}
