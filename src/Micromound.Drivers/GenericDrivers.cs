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
/// <para><b>Timed hold, bounded and fail-safe.</b> An execution drives the line to its active level
/// and HOLDS it for <c>on_s</c>, then releases it — so a real valve is actually open for its duration,
/// not pulsed for a microsecond. The hold is driven by the clock, not a private thread: the service
/// loop calls <see cref="ServiceHolds"/> every tick, which releases the line once the deadline passes.
/// The hold is bounded on every side — <c>on_s</c> arrives already clamped to the intersected limit
/// tiers, and it is capped again here at the effective <c>max_on_s</c> as a last-resort belt — and it
/// is released by <see cref="EnterSafeState"/> on any stop, quiesce, shutdown, or trip. Because the
/// line is deliberately held active between ticks, the release granularity is one tick: the real hold
/// can run up to one tick interval past <c>on_s</c>, so a hard hardware bound should carry that margin.
/// If the release write itself fails the hold stays pending (the next tick retries) and the failure is
/// escalated to a trip.</para>
///
/// <para><b>The safety trade this makes, and its one gap.</b> Unlike the momentary primitive it
/// replaces, a timed hold intentionally relies on a later action to de-energize the line, so its
/// safety depends on the service loop continuing to tick. Every ORDERLY path is covered — a stop,
/// quiesce, shutdown, trip, or a mere slow tick all release the line — and a stuck line escalates to a
/// persisted stop. The remaining gap is a fully <em>hung</em> loop: the kernel's stale-heartbeat rule
/// still refuses NEW actuations, but it cannot release a line already held, so a hang can leave a line
/// energized until it is torn down (a restart de-energizes at configure time). Closing that gap is the
/// job of the dedicated watchdog thread — a separate hardware-independent timer that this design
/// elevates from a nicety to a prerequisite before a mound holds real loads unattended. Until it
/// lands, keep <c>max_on_s</c> conservative and the tick interval short. SAFETY.md tracks this.</para>
/// </summary>
public sealed class DigitalActuatorDriver : GenericDriverBase, ITimedDriver
{
    private readonly Func<IReadOnlyDictionary<string, string>, IDigitalOutput> _portBuilder;
    private IDigitalOutput? _output;
    private string _capability = "";
    private bool _activeHigh = true;
    private CapabilityDescriptor? _descriptor;
    private Executor? _executor;
    private DateTimeOffset? _heldUntil;

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

    /// <summary>True while the line is held active awaiting its timed release, for a test or health view.</summary>
    public bool IsHolding => _heldUntil is not null;

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
        _output = null;      // drop any port a prior configuration opened, so a failed reconfigure holds no line
        _heldUntil = null;   // a reconfigure ends any hold; the port drops with it
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

    // Null-guarded: an unconfigured or failed driver has no line to make safe. This is the release
    // path for a stop, quiesce, shutdown, or trip — it drives the line to safe and ends any hold. If
    // the port write throws, the hold stays pending (so ServiceHolds and any later safe-state call
    // both keep trying) and the exception propagates for the host to escalate to a trip; a line that
    // will not de-energize must be treated as unsafe (SAFETY.md).
    public override void EnterSafeState()
    {
        _output?.Write(!_activeHigh);
        _heldUntil = null;   // reached only if the write did not throw: the line is safe, the hold is over
    }

    /// <summary>
    /// Release the held line once its duration has elapsed — the clock-driven half of the timed hold,
    /// called by the service loop each tick. Idempotent: with no active hold, or before the deadline,
    /// it does nothing. If the release write throws the hold is LEFT pending (the next tick retries)
    /// and the exception propagates, so the host turns a stuck line into a sticky trip.
    /// </summary>
    public void ServiceHolds(DateTimeOffset now)
    {
        if (_heldUntil is null || now < _heldUntil.Value)
            return;
        _output?.Write(!_activeHigh);   // may throw → hold stays pending, host trips
        _heldUntil = null;
    }

    /// <summary>Drive to the safe level, swallowing any port error — a last-resort de-energize that
    /// must never itself throw. A real port write can fail (a yanked line, a transient sysfs error);
    /// this is the most we can do in-band, and the outcome is reported as a fault regardless.</summary>
    private void DriveSafeBestEffort()
    {
        try { _output?.Write(!_activeHigh); }
        catch { /* the line is not controllable; the fault outcome carries that upward */ }
        _heldUntil = null;
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

            // on_s is already clamped to the intersected limit tiers (CapabilityExecution: "effective
            // values, not requested ones"). Cap it again at the effective max_on_s here as a last-resort
            // belt, so even a contract violation upstream can never hold the line beyond the hardware
            // bound. A non-positive or non-finite duration is not a hold — refuse it.
            var hold = onSeconds;
            if (execution.EffectiveLimits.MaxOnSeconds is { } max && hold > max)
                hold = max;
            if (!double.IsFinite(hold) || hold <= 0)
                return ExecutionOutcome.Fault($"digital actuator needs a positive, bounded 'on_s', not {onSeconds}");

            // Drive the line active and HOLD it. The executor exists only while Health is Healthy, which
            // is exactly when _output is set. A real GPIO write can THROW (an in-memory line never did);
            // if the energize fails, nothing was actuated — drive safe best-effort and fault, never
            // propagate with the line possibly latched. On success the line is now held; ServiceHolds
            // (each tick) or EnterSafeState (any stop) releases it.
            try
            {
                driver._output!.Write(driver._activeHigh);
            }
            catch (Exception ex)
            {
                driver.DriveSafeBestEffort();
                return ExecutionOutcome.Fault("actuator could not drive its line active: " + ex.Message);
            }

            driver.Actuations++;
            driver.LastOnSeconds = hold;
            driver._heldUntil = execution.StartedAt + TimeSpan.FromSeconds(hold);

            // No evidence: a command is not evidence. A separate sensor device confirms the effect,
            // or the outcome stays unverified. That is the gate doing its job, not a gap here. The
            // kernel infers EndedAt from the duration parameter, so duty-cycle accounting already spans
            // the hold; the hardware now matches that model instead of pulsing.
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
