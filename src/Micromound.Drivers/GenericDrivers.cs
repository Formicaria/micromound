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

    /// <summary>Convenience for a port that needs no settings and owns no line (the in-memory simulator
    /// line, tests) — the same instance is reused across reconfigures.</summary>
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
        // Drop any port a prior configuration opened — and RELEASE it if it owns a line (a chardev line
        // stays claimed until its descriptor closes; re-requesting the same pin would be EBUSY). The
        // convenience ctor's fixed in-memory line is not disposable, so it is simply reused.
        (_output as IDisposable)?.Dispose();
        _output = null;      // so a failed reconfigure holds no line
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
///
/// <para><b>The channel is opened from the manifest at configure time</b>, like the actuator's line:
/// a real ADC channel needs its bus, address, and channel number, which are only known once the
/// manifest slice is applied. A channel that cannot be opened — no bus, a chip that does not answer
/// — is a fail-closed configuration refusal, so an empty I2C address never becomes a sensor that
/// reads zero.</para>
///
/// <para><b>Optional linear calibration.</b> A real channel yields <em>volts</em>; a threshold in a
/// charter is written in the sensor's own unit. Optional <c>scale</c> and <c>offset</c> settings map
/// <c>value = raw × scale + offset</c> (defaults 1 and 0), both required finite so a bad manifest
/// cannot turn every reading into NaN. A reading that fails (the chip stopped answering) is a
/// <b>fault</b>, never a fabricated number: no reading is emitted and the executor reports why.</para>
/// </summary>
public sealed class AnalogSensorDriver : GenericDriverBase
{
    private readonly Func<IReadOnlyDictionary<string, string>, IAnalogInput> _channelBuilder;
    private IAnalogInput? _input;
    private string _capability = "";
    private string _unit = "";
    private double _scale = 1;
    private double _offset;
    private CapabilityDescriptor? _descriptor;
    private Executor? _executor;

    /// <summary>
    /// The channel is built from the manifest's settings at <see cref="OnConfigure"/> time; the
    /// builder MUST return a fresh channel per call and should THROW if the hardware is not there.
    /// </summary>
    public AnalogSensorDriver(Func<IReadOnlyDictionary<string, string>, IAnalogInput> channelBuilder) =>
        _channelBuilder = channelBuilder;

    /// <summary>Convenience for a channel that needs no settings and owns no device node (the in-memory
    /// simulator channel, tests) — the same instance is reused across reconfigures.</summary>
    public AnalogSensorDriver(IAnalogInput input) : this(_ => input) { }

    public override string DriverId => "analog_sensor:" + _capability;
    public override string Bus => BusKinds.I2c;

    /// <summary>The calibration in force, for a test or a health view.</summary>
    public double Scale => _scale;
    public double Offset => _offset;

    public override IReadOnlyList<CapabilityDescriptor> Capabilities =>
        _descriptor is null ? [] : [_descriptor];

    public override IReadOnlyList<ICapabilityExecutor> Executors =>
        _executor is null ? [] : [_executor];

    protected override void Reset()
    {
        _capability = "";
        _unit = "";
        _scale = 1;
        _offset = 0;
        _descriptor = null;
        _executor = null;
        // Drop any channel a prior configuration opened — and CLOSE it if it owns a device node, so a
        // reconfigure does not leak a file descriptor per attempt.
        (_input as IDisposable)?.Dispose();
        _input = null;
    }

    protected override void OnConfigure(IReadOnlyDictionary<string, string> settings, List<string> errors)
    {
        var capability = Required(settings, "capability", errors);
        var wellFormed = ValidCapability(capability, "sense.", "an analog sensor", errors);

        _unit = settings.TryGetValue("unit", out var unit) ? unit : "";
        _scale = OptionalFinite(settings, "scale", fallback: 1, errors);
        _offset = OptionalFinite(settings, "offset", fallback: 0, errors);

        if (errors.Count > 0 || !wellFormed)
            return;

        // Open the channel from the manifest settings LAST, once the slice has otherwise validated. A
        // channel that cannot be opened (no bus, no chip at the address, a bad channel number) is a
        // fail-closed refusal — the driver stays Absent and the kernel never samples a phantom sensor.
        try
        {
            _input = _channelBuilder(settings);
        }
        catch (Exception ex)
        {
            errors.Add("could not open the sensor's channel: " + ex.Message);
            return;
        }

        _capability = capability!;
        _descriptor = new CapabilityDescriptor
        {
            Id = capability!,
            Class = ActionClass.Observe,
            Description = "analog sensor " + capability
        };
        _executor = new Executor(this);
    }

    /// <summary>Parse an optional finite number (any sign); a non-number or NaN/Infinity fails closed.</summary>
    private static double OptionalFinite(IReadOnlyDictionary<string, string> settings, string key, double fallback, List<string> errors)
    {
        if (!settings.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            errors.Add($"setting '{key}' is not a number: '{raw}'");
            return fallback;
        }
        if (!double.IsFinite(parsed))
        {
            errors.Add($"setting '{key}' must be a finite number, not '{raw}'");
            return fallback;
        }
        return parsed;
    }

    public override void EnterSafeState() { /* a sensor has no output to make safe */ }

    private sealed class Executor(AnalogSensorDriver driver) : ICapabilityExecutor
    {
        public string CapabilityId => driver._capability;
        public bool IsAvailable => driver.Health == DriverHealth.Healthy;

        public ExecutionOutcome Execute(CapabilityExecution execution)
        {
            // The executor exists only while Health is Healthy, which is exactly when _input is set. A
            // real channel read can THROW (an in-memory one never did): a chip that stopped answering is
            // a fault with no reading — never a zero that looks like a measurement.
            double raw;
            try
            {
                raw = driver._input!.Read();
            }
            catch (Exception ex)
            {
                return ExecutionOutcome.Fault("sensor read failed: " + ex.Message);
            }

            var value = raw * driver._scale + driver._offset;
            if (!double.IsFinite(value))
                return ExecutionOutcome.Fault($"sensor produced a non-finite reading ({raw})");

            var item = EvidenceReadings.Create(
                Guid.NewGuid().ToString(), driver._capability, value,
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
    public DriverTypeSchema Schema => DriverSchemaCatalog.DigitalActuator;
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
        var pin = GpioSettings.Pin(settings, "a sysfs digital actuator");
        // sysfs numbering is global (chip base + offset), so a manifest written for the character
        // device with a non-default chip means a DIFFERENT pin here — refuse rather than guess.
        var chip = GpioSettings.Chip(settings, fallback: 0);
        if (chip != 0)
            throw new ArgumentException($"the sysfs GPIO backing has no chip selection ('chip' = {chip}); use --gpio chardev, or the global sysfs pin number with no 'chip'");
        return new SysfsDigitalOutput(pin, sysfsRoot, initialHigh: GpioSettings.SafeLevel(settings));
    });

    public string DriverType => _inner.DriverType;
    public IDriver Create() => _inner.Create();
    public DriverTypeSchema Schema => _inner.Schema;
}

/// <summary>
/// The preferred hardware-backed digital-actuator factory: each actuator over a real GPIO line on the
/// Linux GPIO character device (<see cref="GpioChardevOutput"/>, <c>/dev/gpiochipN</c>) — the interface
/// libgpiod uses and the one the kernel supports going forward. Reads the line's <c>pin</c> (offset)
/// and <c>chip</c> (default 0) from the manifest, and requests the line already at the SAFE level
/// (<c>!active_high</c>) so an active-low load is never energized for an instant at bring-up. Same
/// driver kind, capabilities, limits and polarity as the in-memory default — only the port backing
/// changes. The system-call seam is injectable so the request encoding is tested against a fake.
/// </summary>
public sealed class GpioChardevActuatorFactory(int defaultChip = 0, ILinuxIo? io = null) : IDriverFactory
{
    private readonly DigitalActuatorFactory _inner = new(settings =>
    {
        var line = GpioSettings.Pin(settings, "a GPIO character-device actuator");
        var chip = GpioSettings.Chip(settings, defaultChip);
        return new GpioChardevOutput(line, initialHigh: GpioSettings.SafeLevel(settings), chip, io);
    });

    public string DriverType => _inner.DriverType;
    public IDriver Create() => _inner.Create();
    public DriverTypeSchema Schema => _inner.Schema;
}

/// <summary>The GPIO backings the daemon can compose (<c>--gpio</c>).</summary>
public static class GpioBackings
{
    /// <summary>The GPIO character device, <c>/dev/gpiochipN</c> — the default and the supported path.</summary>
    public const string Chardev = "chardev";
    /// <summary>Legacy <c>/sys/class/gpio</c>, for kernels that still ship it.</summary>
    public const string Sysfs = "sysfs";

    public static bool IsKnown(string? backing) => backing is Chardev or Sysfs;
}

/// <summary>The manifest settings the two GPIO backings share, parsed the same way by both.</summary>
internal static class GpioSettings
{
    public static int Pin(IReadOnlyDictionary<string, string> settings, string what)
    {
        if (!settings.TryGetValue("pin", out var raw) || string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException($"{what} requires a 'pin' setting");
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pin))
            throw new ArgumentException($"'pin' is not an integer: '{raw}'");
        return pin;
    }

    public static int Chip(IReadOnlyDictionary<string, string> settings, int fallback)
    {
        if (!settings.TryGetValue("chip", out var raw) || string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chip))
            throw new ArgumentException($"'chip' is not an integer: '{raw}'");
        return chip;
    }

    /// <summary>The SAFE level of the line — the opposite of its active level. Parsed exactly as the
    /// driver parses <c>active_high</c> (default true); an unparseable value is left to the driver to refuse.</summary>
    public static bool SafeLevel(IReadOnlyDictionary<string, string> settings)
    {
        var activeHigh = !settings.TryGetValue("active_high", out var raw) || string.IsNullOrWhiteSpace(raw)
                         || !bool.TryParse(raw, out var parsed) || parsed;
        return !activeHigh;
    }
}

/// <summary>
/// Builds <see cref="AnalogSensorDriver"/> instances. Each driver opens its channel from the manifest
/// settings at configure time via the injected <em>channel builder</em> — an in-memory channel in the
/// simulator and tests, a real ADC channel (<see cref="Ads1115AnalogInput"/>) on a device. The builder
/// MUST return a NEW channel per call, for the same reason as the actuator's port builder.
/// </summary>
public sealed class AnalogSensorFactory(Func<IReadOnlyDictionary<string, string>, IAnalogInput> channelBuilder) : IDriverFactory
{
    /// <summary>Defaults to a fresh in-memory channel per driver, for the simulator and tests.</summary>
    public AnalogSensorFactory() : this(_ => new InMemoryAnalogInput()) { }

    /// <summary>A settings-free channel factory, adapted to the builder shape.</summary>
    public AnalogSensorFactory(Func<IAnalogInput> channelFactory) : this(_ => channelFactory()) { }

    public string DriverType => "analog_sensor";
    public IDriver Create() => new AnalogSensorDriver(channelBuilder);
    public DriverTypeSchema Schema => DriverSchemaCatalog.AnalogSensor;
}

/// <summary>
/// The hardware-backed analog-sensor factory: it builds each sensor over one channel of a real
/// ADS1115 on the Linux I2C bus (<see cref="Ads1115AnalogInput"/> over <see cref="LinuxI2cBus"/>),
/// reading the chip's location from the manifest settings. Same driver kind (<c>analog_sensor</c>),
/// same capability, unit, and calibration settings as the in-memory default — only the channel
/// backing changes. A device's registry uses it in place of <see cref="AnalogSensorFactory"/>.
///
/// <para>Settings: <c>channel</c> (required, 0..3), <c>bus</c> (default 1, the Pi's header bus),
/// <c>address</c> (decimal or <c>0x</c> hex, default 0x48), <c>gain</c> (a full-scale range in volts
/// from <see cref="Ads1115AnalogInput.FullScaleRanges"/>, default 4.096). Anything malformed, and a
/// chip that does not answer, throws at open time — a fail-closed configuration refusal.</para>
/// </summary>
public sealed class Ads1115AnalogSensorFactory : IDriverFactory
{
    private readonly AnalogSensorFactory _inner;

    /// <param name="defaultBus">The I2C bus used when a slice does not name one. The Pi's is 1.</param>
    /// <param name="busFactory">How a bus is opened, injectable so the setting parsing can be tested
    /// with a fake device; defaults to <see cref="LinuxI2cBus"/>.</param>
    public Ads1115AnalogSensorFactory(int defaultBus = 1, Func<int, int, II2cBus>? busFactory = null)
    {
        var openBus = busFactory ?? ((bus, address) => new LinuxI2cBus(bus, address));
        _inner = new AnalogSensorFactory(settings =>
        {
            // Validate every setting BEFORE touching the bus: a slice with a bad channel or gain is
            // refused without opening (and then leaking) a device node.
            var channel = RequiredInt(settings, "channel");
            if (channel is < 0 or > 3)
                throw new ArgumentException($"'channel' must be 0..3 on an ADS1115, not {channel}");
            var bus = OptionalInt(settings, "bus", defaultBus);
            var address = OptionalInt(settings, "address", Ads1115AnalogInput.DefaultAddress);
            var gain = 4.096;
            if (settings.TryGetValue("gain", out var gainRaw) && !string.IsNullOrWhiteSpace(gainRaw))
            {
                if (!double.TryParse(gainRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out gain)
                    || Ads1115AnalogInput.IndexOfRange(gain) < 0)
                    throw new ArgumentException($"'gain' must be one of {string.Join(", ", Ads1115AnalogInput.FullScaleRanges)} volts, not '{gainRaw}'");
            }

            var device = openBus(bus, address);
            try
            {
                return new Ads1115AnalogInput(device, channel, gain);   // probes; throws if the chip is absent
            }
            catch
            {
                (device as IDisposable)?.Dispose();   // never leak the device node behind a refusal
                throw;
            }
        });
    }

    public string DriverType => _inner.DriverType;
    public IDriver Create() => _inner.Create();
    public DriverTypeSchema Schema => _inner.Schema;

    private static int RequiredInt(IReadOnlyDictionary<string, string> settings, string key)
    {
        if (!settings.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException($"an ADS1115 analog sensor requires a '{key}' setting");
        return ParseInt(key, raw);
    }

    private static int OptionalInt(IReadOnlyDictionary<string, string> settings, string key, int fallback) =>
        settings.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? ParseInt(key, raw) : fallback;

    /// <summary>Decimal, or hex with a <c>0x</c> prefix — an I2C address is conventionally written as hex.</summary>
    private static int ParseInt(string key, string raw)
    {
        var text = raw.Trim();
        var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        if (!ok)
            throw new ArgumentException($"'{key}' is not an integer: '{raw}'");
        return value;
    }
}
