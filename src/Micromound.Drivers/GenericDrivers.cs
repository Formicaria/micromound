using System.Globalization;
using Micromound.Capabilities;
using Micromound.Protocol;

namespace Micromound.Drivers;

/// <summary>
/// Shared configuration plumbing for the generic driver primitives: string-settings parsing that
/// fails closed, a health flag that a bad configuration turns off, and the safe-state bookkeeping
/// every driver owes. Concrete primitives add the one operation they turn a capability into.
/// </summary>
public abstract class GenericDriverBase : IDriver, IEvidenceSource
{
    private readonly List<string> _errors = [];

    public abstract string DriverId { get; }
    public abstract string Bus { get; }
    public DriverHealth Health { get; private set; } = DriverHealth.Healthy;

    public abstract IReadOnlyList<CapabilityDescriptor> Capabilities { get; }
    public abstract IReadOnlyList<ICapabilityExecutor> Executors { get; }

    /// <summary>Receives every reading this driver's hardware observes. Wired by composition.</summary>
    public Action<EvidenceItem>? Publish { get; set; }

    public ValidationResult Configure(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _errors.Clear();

        // Fully clear any prior configuration FIRST, so a failed reconfigure fails closed rather
        // than leaving a stale descriptor and executor exposed while Health reads Absent.
        Reset();
        OnConfigure(settings, _errors);

        // Fail closed: a driver that could not parse its manifest slice never initializes, and the
        // kernel refuses its capability as unavailable rather than acting on an unconfigured device.
        if (_errors.Count > 0)
        {
            Reset();   // discard anything a partially-successful OnConfigure may have set
            Health = DriverHealth.Absent;
            return new ValidationResult([.. _errors]);
        }

        Health = DriverHealth.Healthy;
        return new ValidationResult([]);
    }

    /// <summary>Drop all configured state, so an unconfigured or failed driver exposes nothing.</summary>
    protected abstract void Reset();

    protected abstract void OnConfigure(IReadOnlyDictionary<string, string> settings, List<string> errors);

    public abstract void EnterSafeState();

    /// <summary>Require a setting; record an error and return null when it is missing or blank.</summary>
    protected static string? Required(IReadOnlyDictionary<string, string> settings, string key, List<string> errors)
    {
        if (settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        errors.Add($"setting '{key}' is required");
        return null;
    }

    /// <summary>
    /// Parse an optional non-negative, finite limit (seconds or a rate). A non-number, a negative,
    /// or NaN/Infinity fails closed with an error — a bad value in the hardware limit tier is the
    /// one place it must never pass, because <c>Math.Min</c> propagates NaN and would neutralize the
    /// device and charter tiers layered under it.
    /// </summary>
    protected static double? OptionalLimit(IReadOnlyDictionary<string, string> settings, string key, List<string> errors)
    {
        if (!settings.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            errors.Add($"setting '{key}' is not a number: '{raw}'");
            return null;
        }
        if (!double.IsFinite(parsed) || parsed < 0)
        {
            errors.Add($"setting '{key}' must be a finite, non-negative number, not '{raw}'");
            return null;
        }
        return parsed;
    }

    /// <summary>Parse an optional bool; a present-but-unparseable value fails closed with an error.</summary>
    protected static bool OptionalBool(IReadOnlyDictionary<string, string> settings, string key, bool fallback, List<string> errors)
    {
        if (!settings.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (bool.TryParse(raw, out var parsed))
            return parsed;
        errors.Add($"setting '{key}' must be 'true' or 'false', not '{raw}'");
        return fallback;
    }

    /// <summary>Validate a capability id against the prefix its driver kind requires AND well-formedness.</summary>
    protected static bool ValidCapability(string? capability, string requiredPrefix, string kind, List<string> errors)
    {
        if (capability is null)
            return false;
        if (!capability.StartsWith(requiredPrefix, StringComparison.Ordinal))
        {
            errors.Add($"{kind}'s capability must be a '{requiredPrefix}' capability, not '{capability}'");
            return false;
        }
        if (!CapabilityId.IsWellFormed(capability))
        {
            errors.Add($"capability '{capability}' is not a well-formed capability id");
            return false;
        }
        return true;
    }
}

/// <summary>
/// A generic binary actuator over one <see cref="IDigitalOutput"/> line — the primitive a water
/// valve, a heater relay, or a solenoid is configured from, with no device-specific type of its own.
/// It exposes one <c>act.</c> capability whose hardware limits come from the manifest, and it
/// produces no evidence of its own: a command is not evidence, so an actuator with no independent
/// sensor leaves its outcome <c>unverified</c> until a separate sensor device confirms it.
///
/// <para><b>Momentary and fail-safe.</b> Without a hardware scheduler this primitive cannot hold the
/// line active for <c>on_s</c> and guarantee it releases, so it drives the line to its active level
/// and back to its safe level within one execution rather than latching a line hot that only a later
/// <see cref="EnterSafeState"/> could clear. Holding for a real duration is the timed-driver work of
/// the hardware slice; the effective <c>on_s</c> is required and recorded, never defaulted.</para>
/// </summary>
public sealed class DigitalActuatorDriver : GenericDriverBase
{
    private readonly Func<IReadOnlyDictionary<string, string>, IDigitalOutput> _portBuilder;
    private IDigitalOutput? _output;
    private string _capability = "";
    private bool _activeHigh = true;
    private CapabilityDescriptor? _descriptor;
    private Executor? _executor;

    /// <summary>
    /// The port is built from the manifest's settings at <see cref="OnConfigure"/> time, not at
    /// construction: a real GPIO line needs the <c>pin</c> setting, which is not known until the
    /// manifest slice is applied. The builder MUST return a fresh line per call.
    /// </summary>
    public DigitalActuatorDriver(Func<IReadOnlyDictionary<string, string>, IDigitalOutput> portBuilder) =>
        _portBuilder = portBuilder;

    /// <summary>Convenience for a port that needs no settings (the in-memory simulator line, tests).</summary>
    public DigitalActuatorDriver(IDigitalOutput output) : this(_ => output) { }

    public override string DriverId => "digital_actuator:" + _capability;
    public override string Bus => BusKinds.Gpio;

    /// <summary>How many times the line was driven active — the fake world's ground truth for a test.</summary>
    public int Actuations { get; private set; }

    /// <summary>Effective seconds of the last actuation, for a test or a health view.</summary>
    public double LastOnSeconds { get; private set; }

    public override IReadOnlyList<CapabilityDescriptor> Capabilities =>
        _descriptor is null ? [] : [_descriptor];

    public override IReadOnlyList<ICapabilityExecutor> Executors =>
        _executor is null ? [] : [_executor];

    protected override void Reset()
    {
        _capability = "";
        _activeHigh = true;
        _descriptor = null;
        _executor = null;
        _output = null;   // drop any port a prior configuration opened, so a failed reconfigure holds no line
    }

    protected override void OnConfigure(IReadOnlyDictionary<string, string> settings, List<string> errors)
    {
        var capability = Required(settings, "capability", errors);
        var wellFormed = ValidCapability(capability, "act.", "a digital actuator", errors);

        _activeHigh = OptionalBool(settings, "active_high", fallback: true, errors);

        // An actuator may be pinned to a higher class than the benign default, but never hazardous
        // (which the registry refuses to register at all).
        var actionClass = ActionClass.Benign;
        if (settings.TryGetValue("class", out var classRaw) && !string.IsNullOrWhiteSpace(classRaw))
        {
            if (!ActionClasses.TryParse(classRaw, out actionClass))
                errors.Add($"setting 'class' is not a known action class: '{classRaw}'");
            else if (actionClass == ActionClass.Observe)
                errors.Add("a digital actuator cannot be class 'observe'");
            else if (actionClass == ActionClass.Hazardous)
                errors.Add("a digital actuator cannot be class 'hazardous'");
        }

        var limits = new CapabilityLimits
        {
            MaxOnSeconds = OptionalLimit(settings, "max_on_s", errors),
            MinOffSeconds = OptionalLimit(settings, "min_off_s", errors),
            MaxRatePerHour = OptionalLimit(settings, "max_rate_per_h", errors)
        };

        if (errors.Count > 0 || !wellFormed)
            return;

        // Open the hardware port from the manifest settings LAST, once the slice has otherwise
        // validated. A port that cannot be opened (a bad pin, a busy line, no GPIO on this host) is a
        // fail-closed refusal — the driver stays Absent and the kernel never acts on an unbacked line.
        try
        {
            _output = _portBuilder(settings);
        }
        catch (Exception ex)
        {
            errors.Add("could not open the actuator's hardware port: " + ex.Message);
            return;
        }

        _capability = capability!;
        _descriptor = new CapabilityDescriptor
        {
            Id = capability!,
            Class = actionClass,
            Description = "digital actuator " + capability,
            HardwareLimits = limits,
            Parameters = new HashSet<string>(StringComparer.Ordinal) { "on_s" },
            RequiredParameters = new HashSet<string>(StringComparer.Ordinal) { "on_s" },
            DurationParameter = "on_s"
        };
        _executor = new Executor(this);
        _output.Write(!_activeHigh);   // start in the safe (inactive) level
    }

    // Null-guarded: an unconfigured or failed driver has no line to make safe, and being asked to
    // enter safe state must never itself throw.
    public override void EnterSafeState() => _output?.Write(!_activeHigh);

    /// <summary>Drive to the safe level, swallowing any port error — a last-resort de-energize that
    /// must never itself throw. A real port write can fail (a yanked line, a transient sysfs error);
    /// this is the most we can do in-band, and the outcome is reported as a fault regardless.</summary>
    private void DriveSafeBestEffort()
    {
        try { _output?.Write(!_activeHigh); }
        catch { /* the line is not controllable; the fault outcome carries that upward */ }
    }

    private sealed class Executor(DigitalActuatorDriver driver) : ICapabilityExecutor
    {
        public string CapabilityId => driver._capability;
        public bool IsAvailable => driver.Health == DriverHealth.Healthy;

        public ExecutionOutcome Execute(CapabilityExecution execution)
        {
            // on_s is required, so the kernel guarantees it is present here; treat its absence as a
            // fault rather than a silent zero-length latch.
            if (!execution.Parameters.TryGetValue("on_s", out var onSeconds))
                return ExecutionOutcome.Fault("digital actuator requires an 'on_s' duration");

            driver.Actuations++;
            driver.LastOnSeconds = onSeconds;

            // Drive active, then back to safe within the one execution: never leave the line hot
            // relying on a later EnterSafeState. A real timed driver holds for on_s; this does not.
            // The executor exists only while Health is Healthy, which is exactly when _output is set.
            //
            // A real GPIO write can THROW (an in-memory line never did). Both writes are guarded so a
            // failure never leaves a physical line latched hot with an exception sailing past: if the
            // energize fails nothing was actuated; if the RELEASE fails — the dangerous case — we make
            // one more best-effort attempt to drive safe and return a fault, never propagate with the
            // line possibly still active.
            try
            {
                driver._output!.Write(driver._activeHigh);
            }
            catch (Exception ex)
            {
                driver.DriveSafeBestEffort();
                return ExecutionOutcome.Fault("actuator could not drive its line active: " + ex.Message);
            }

            try
            {
                driver._output!.Write(!driver._activeHigh);
            }
            catch (Exception ex)
            {
                driver.DriveSafeBestEffort();
                return ExecutionOutcome.Fault("actuator could not release its line after actuating: " + ex.Message);
            }

            // No evidence: a command is not evidence. A separate sensor device confirms the effect,
            // or the outcome stays unverified. That is the gate doing its job, not a gap here.
            return ExecutionOutcome.Ok();
        }
    }
}

/// <summary>
/// A generic analog sensor over one <see cref="IAnalogInput"/> channel — the primitive a soil-
/// moisture probe, a thermistor, or any single-number sensor is configured from. It exposes one
/// <c>sense.</c> capability, and its reading IS the evidence: every execution samples the channel
/// and emits a numeric reading the Witness Ant can later correlate.
/// </summary>
public sealed class AnalogSensorDriver(IAnalogInput input) : GenericDriverBase
{
    private readonly IAnalogInput _input = input;
    private string _capability = "";
    private string _unit = "";
    private CapabilityDescriptor? _descriptor;
    private Executor? _executor;

    public override string DriverId => "analog_sensor:" + _capability;
    public override string Bus => BusKinds.I2c;

    public override IReadOnlyList<CapabilityDescriptor> Capabilities =>
        _descriptor is null ? [] : [_descriptor];

    public override IReadOnlyList<ICapabilityExecutor> Executors =>
        _executor is null ? [] : [_executor];

    protected override void Reset()
    {
        _capability = "";
        _unit = "";
        _descriptor = null;
        _executor = null;
    }

    protected override void OnConfigure(IReadOnlyDictionary<string, string> settings, List<string> errors)
    {
        var capability = Required(settings, "capability", errors);
        var wellFormed = ValidCapability(capability, "sense.", "an analog sensor", errors);

        _unit = settings.TryGetValue("unit", out var unit) ? unit : "";

        if (errors.Count > 0 || !wellFormed)
            return;

        _capability = capability!;
        _descriptor = new CapabilityDescriptor
        {
            Id = capability!,
            Class = ActionClass.Observe,
            Description = "analog sensor " + capability
        };
        _executor = new Executor(this);
    }

    public override void EnterSafeState() { /* a sensor has no output to make safe */ }

    private sealed class Executor(AnalogSensorDriver driver) : ICapabilityExecutor
    {
        public string CapabilityId => driver._capability;
        public bool IsAvailable => driver.Health == DriverHealth.Healthy;

        public ExecutionOutcome Execute(CapabilityExecution execution)
        {
            var item = EvidenceReadings.Create(
                Guid.NewGuid().ToString(), driver._capability, driver._input.Read(),
                execution.StartedAt, unit: driver._unit, source: driver.DriverId);

            driver.Publish?.Invoke(item);
            return ExecutionOutcome.Ok([item]);
        }
    }
}

/// <summary>
/// Builds <see cref="DigitalActuatorDriver"/> instances. Each driver opens its output line from the
/// manifest settings at configure time via the injected <em>port builder</em> — an in-memory line in
/// the simulator and tests, a real GPIO line (<see cref="SysfsDigitalOutput"/>) on a device — so this
/// one factory serves every digital actuator a manifest declares.
///
/// <para>The port builder MUST return a NEW port per call: the driver calls it once each time its
/// slice is (re)configured, and a builder that captured a single shared instance would wire every
/// actuator to one line. The settings-taking overload is what lets a real port read its <c>pin</c>
/// (or bus address) from the manifest; the settings-free overloads ignore the settings.</para>
/// </summary>
public sealed class DigitalActuatorFactory(Func<IReadOnlyDictionary<string, string>, IDigitalOutput> portBuilder) : IDriverFactory
{
    /// <summary>Defaults to a fresh in-memory line per driver, for the simulator and tests.</summary>
    public DigitalActuatorFactory() : this(_ => new InMemoryDigitalOutput()) { }

    /// <summary>A settings-free port factory (e.g. a fixed fake line), adapted to the builder shape.</summary>
    public DigitalActuatorFactory(Func<IDigitalOutput> portFactory) : this(_ => portFactory()) { }

    public string DriverType => "digital_actuator";
    public IDriver Create() => new DigitalActuatorDriver(portBuilder);
}

/// <summary>
/// The hardware-backed digital-actuator factory: it builds each actuator over a real Linux GPIO line
/// (<see cref="SysfsDigitalOutput"/>), reading the line's <c>pin</c> from the manifest settings. This
/// is the factory a device's driver registry uses in place of <see cref="DigitalActuatorFactory"/>'s
/// in-memory default; the driver kind (<c>digital_actuator</c>) and every capability, limit, and
/// polarity setting are identical — only the port backing changes.
///
/// <para>The sysfs root is injectable so the pin-parsing and file protocol can be exercised against a
/// fake tree; on a device it defaults to <c>/sys/class/gpio</c>. A missing or non-integer <c>pin</c>
/// throws at open time, which the driver turns into a fail-closed configuration refusal.</para>
/// </summary>
public sealed class SysfsDigitalActuatorFactory(string sysfsRoot = "/sys/class/gpio") : IDriverFactory
{
    private readonly DigitalActuatorFactory _inner = new(settings =>
    {
        if (!settings.TryGetValue("pin", out var raw) || string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("a sysfs digital actuator requires a 'pin' setting");
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pin))
            throw new ArgumentException($"'pin' is not an integer: '{raw}'");
        return new SysfsDigitalOutput(pin, sysfsRoot);
    });

    public string DriverType => _inner.DriverType;
    public IDriver Create() => _inner.Create();
}

/// <summary>
/// Builds <see cref="AnalogSensorDriver"/> instances over the injected channel factory, which MUST
/// return a new channel per call (as the parameterless default does).
/// </summary>
public sealed class AnalogSensorFactory(Func<IAnalogInput> channelFactory) : IDriverFactory
{
    /// <summary>Defaults to a fresh in-memory channel per driver, for the simulator and tests.</summary>
    public AnalogSensorFactory() : this(() => new InMemoryAnalogInput()) { }

    public string DriverType => "analog_sensor";
    public IDriver Create() => new AnalogSensorDriver(channelFactory());
}
