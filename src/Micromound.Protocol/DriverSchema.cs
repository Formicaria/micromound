using System.Text.Json.Serialization;

namespace Micromound.Protocol;

/// <summary>
/// The value kinds a driver setting can have (<see cref="DriverSettingSchema.Kind"/>). Every setting
/// travels in the manifest as a STRING (CONFIGURATION.md "Driver settings are strings"); the kind says
/// how a controller should let a person enter it and how the driver will parse it. Deliberately small:
/// a form builder needs a handful of widgets, not a type system.
/// </summary>
public static class SettingKinds
{
    /// <summary>Free text.</summary>
    public const string Text = "text";
    /// <summary>A whole number; decimal, or <c>0x</c>-prefixed hex where the setting says so (an I2C address).</summary>
    public const string Integer = "integer";
    /// <summary>A real number, parsed invariant-culture; <c>NaN</c>/<c>Infinity</c> are refused by every driver.</summary>
    public const string Number = "number";
    /// <summary><c>true</c> or <c>false</c>.</summary>
    public const string Boolean = "boolean";
    /// <summary>One of <see cref="DriverSettingSchema.Choices"/>.</summary>
    public const string Choice = "choice";
    /// <summary>A capability id (CAPABILITIES.md) with the prefix the driver type requires.</summary>
    public const string Capability = "capability";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Text, Integer, Number, Boolean, Choice, Capability
    };
}

/// <summary>
/// One setting a driver type reads from a manifest hardware binding — described so a controller can
/// build a plain-language form for it instead of hand-matching setting names. This is documentation
/// made machine-readable, not authority: the DRIVER still parses and validates the string it is handed
/// and fails closed on anything it cannot accept, whatever a form allowed. A controller that validates
/// against this schema first just gives the person a better error, earlier.
/// </summary>
public sealed class DriverSettingSchema
{
    /// <summary>The key in <see cref="HardwareBinding.Settings"/>, e.g. <c>pin</c>.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>What to call it on a form, e.g. "GPIO pin".</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    /// <summary>One or two sentences for a person: what it means and what a sensible value is.</summary>
    [JsonPropertyName("help")] public string Help { get; set; } = "";

    /// <summary>One of <see cref="SettingKinds"/>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = SettingKinds.Text;

    /// <summary>Whether the driver refuses to configure without it (given <see cref="HardwareOnly"/>).</summary>
    [JsonPropertyName("required")] public bool Required { get; set; }

    /// <summary>The value the driver assumes when the setting is absent, as the string a manifest would carry. Null = none.</summary>
    [JsonPropertyName("default")] public string? Default { get; set; }

    /// <summary>Inclusive lower bound for numeric kinds, when the driver enforces one.</summary>
    [JsonPropertyName("min")] public double? Min { get; set; }

    /// <summary>Inclusive upper bound for numeric kinds, when the driver enforces one.</summary>
    [JsonPropertyName("max")] public double? Max { get; set; }

    /// <summary>The legal values for <see cref="SettingKinds.Choice"/>; for <see cref="SettingKinds.Capability"/>, the required prefix.</summary>
    [JsonPropertyName("choices")] public List<string> Choices { get; set; } = [];

    /// <summary>The unit a numeric value is in (<c>s</c>, <c>V</c>, <c>/h</c>), for the form's suffix. Empty = none.</summary>
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";

    /// <summary>
    /// True when the setting is read only by the real-hardware backing (a pin, a bus address) and is
    /// ignored by the in-memory one — so a simulator manifest may omit it, and "required" means
    /// required on a device.
    /// </summary>
    [JsonPropertyName("hardware_only")] public bool HardwareOnly { get; set; }

    /// <summary>True for a setting most manifests leave at its default — a form can fold it under "Advanced".</summary>
    [JsonPropertyName("advanced")] public bool Advanced { get; set; }
}

/// <summary>
/// Everything a controller needs to offer a driver type on a hardware form: what it is, which
/// capability prefix it exposes, and the settings it reads. One per <see cref="HardwareBinding.Driver"/>
/// value this build can instantiate. Sent at enrollment (PROTOCOL.md §3.2, <c>driver_schemas</c>),
/// printed by the daemon (<c>--describe-drivers</c>), and available at compile time to a controller
/// that builds against this library as <see cref="DriverSchemaCatalog.Shipped"/>.
/// </summary>
public sealed class DriverTypeSchema
{
    /// <summary>The value a manifest puts in <see cref="HardwareBinding.Driver"/>, e.g. <c>digital_actuator</c>.</summary>
    [JsonPropertyName("driver_type")] public string DriverType { get; set; } = "";

    /// <summary>What to call it on a form, e.g. "Relay / valve / on-off output".</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    /// <summary>A sentence or two for a person choosing it.</summary>
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";

    /// <summary><c>actuator</c> (exposes an <c>act.</c> capability) or <c>sensor</c> (exposes a <c>sense.</c> capability).</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = "";

    /// <summary>The capability prefix the driver's <c>capability</c> setting must carry.</summary>
    [JsonPropertyName("capability_prefix")] public string CapabilityPrefix { get; set; } = "";

    /// <summary>What backs it when the daemon runs with <c>--hardware</c>, for the form's footnote.</summary>
    [JsonPropertyName("hardware_backing")] public string HardwareBacking { get; set; } = "";

    /// <summary>The settings, in the order a form should show them.</summary>
    [JsonPropertyName("settings")] public List<DriverSettingSchema> Settings { get; set; } = [];

    /// <summary>The names of every setting this driver type reads.</summary>
    public IEnumerable<string> SettingNames() => Settings.Select(s => s.Name);
}

/// <summary>The two roles a driver type can play.</summary>
public static class DriverRoles
{
    public const string Actuator = "actuator";
    public const string Sensor = "sensor";
}

/// <summary>
/// The driver types this version of MicroMound ships, described. This is THE catalog: the driver
/// factories in <c>Micromound.Drivers</c> expose these same instances, a test pins that every setting a
/// driver actually reads appears here with the driver's real default, and the daemon prints it. It
/// lives in the protocol library so the reference controller — which compiles against this library —
/// can build its hardware form from it without a device on the network, and so the enrollment copy a
/// device sends is the same data by construction.
///
/// <para>It describes; it never grants. A manifest is still validated by the drivers themselves, and
/// what a device may DO with the hardware still comes only from a charter through the kernel.</para>
/// </summary>
public static class DriverSchemaCatalog
{
    /// <summary>A binary output line: a relay, a valve, a solenoid, a heater contactor.</summary>
    public static readonly DriverTypeSchema DigitalActuator = new()
    {
        DriverType = "digital_actuator",
        Label = "On/off output (relay, valve, solenoid)",
        Summary = "Drives one output line active for a requested number of seconds and releases it, within the limits below. " +
                  "It produces no evidence of its own — a separate sensor confirms the effect, or the outcome stays unverified.",
        Role = DriverRoles.Actuator,
        CapabilityPrefix = "act.",
        HardwareBacking = "A Linux GPIO output line on the GPIO character device (/dev/gpiochipN, the libgpiod interface; BCM line numbering on a Raspberry Pi), or legacy sysfs.",
        Settings =
        [
            new() { Name = "capability", Label = "What this output is", Kind = SettingKinds.Capability, Required = true, Choices = ["act."],
                    Help = "The act. capability name a charter grants and a mission asks for, e.g. act.water_valve." },
            new() { Name = "pin", Label = "GPIO pin", Kind = SettingKinds.Integer, Required = true, HardwareOnly = true, Min = 0,
                    Help = "The GPIO number (BCM numbering, not the header position) the line is on. Ignored without --hardware." },
            new() { Name = "chip", Label = "GPIO chip", Kind = SettingKinds.Integer, Default = "0", HardwareOnly = true, Advanced = true, Min = 0,
                    Help = "/dev/gpiochip<chip>. The Raspberry Pi header is chip 0 (chip 4 on a Pi 5 with an older 6.1/6.6 kernel). Character-device backing only; the legacy sysfs backing refuses a non-zero chip." },
            new() { Name = "active_high", Label = "Active when high", Kind = SettingKinds.Boolean, Default = "true", Advanced = true,
                    Help = "true if driving the pin high turns the load on; false for an active-low relay board. The safe level is the opposite." },
            new() { Name = "max_on_s", Label = "Longest single run", Kind = SettingKinds.Number, Unit = "s", Min = 0,
                    Help = "The hardware bound on one actuation, in seconds. A charter can only shorten it. Leave empty for no hardware bound (not recommended for a load that can do harm)." },
            new() { Name = "min_off_s", Label = "Minimum rest between runs", Kind = SettingKinds.Number, Unit = "s", Min = 0, Advanced = true,
                    Help = "Seconds the line must stay off after a run before it may run again." },
            new() { Name = "max_rate_per_h", Label = "Most runs per hour", Kind = SettingKinds.Number, Unit = "/h", Min = 0, Advanced = true,
                    Help = "A duty-cycle cap: how many actuations an hour may hold at most." },
            new() { Name = "class", Label = "Action class", Kind = SettingKinds.Choice, Default = "benign", Choices = ["benign", "controlled"], Advanced = true,
                    Help = "How consequential an actuation is; a charter's action ceiling must reach it. 'hazardous' is never accepted for a generic output." },
        ]
    };

    /// <summary>A single-number sensor: a soil probe, a thermistor, a level sensor, a pressure transducer.</summary>
    public static readonly DriverTypeSchema AnalogSensor = new()
    {
        DriverType = "analog_sensor",
        Label = "Analog sensor (probe, thermistor, level, pressure)",
        Summary = "Samples one analog input on request and records the value as evidence. " +
                  "A real channel reads volts; scale and offset turn that into the sensor's own unit so charter thresholds can be written in it.",
        Role = DriverRoles.Sensor,
        CapabilityPrefix = "sense.",
        HardwareBacking = "One input of a TI ADS1115 16-bit ADC on the Linux I2C bus (/dev/i2c-N), single-shot, in volts.",
        Settings =
        [
            new() { Name = "capability", Label = "What this sensor measures", Kind = SettingKinds.Capability, Required = true, Choices = ["sense."],
                    Help = "The sense. capability name a mission reads, e.g. sense.soil_moisture." },
            new() { Name = "channel", Label = "ADC input", Kind = SettingKinds.Integer, Required = true, HardwareOnly = true, Min = 0, Max = 3,
                    Help = "Which ADS1115 input the sensor is wired to: AIN0..AIN3, single-ended against ground." },
            new() { Name = "unit", Label = "Unit", Kind = SettingKinds.Text, Default = "",
                    Help = "Recorded on every reading (pct, C, V, kPa). Informational." },
            new() { Name = "scale", Label = "Scale", Kind = SettingKinds.Number, Default = "1", Advanced = true,
                    Help = "value = volts × scale + offset. A 0..2 V probe reporting 0..100 % has scale 50." },
            new() { Name = "offset", Label = "Offset", Kind = SettingKinds.Number, Default = "0", Advanced = true,
                    Help = "Added after scaling, in the sensor's unit." },
            new() { Name = "bus", Label = "I2C bus", Kind = SettingKinds.Integer, Default = "1", HardwareOnly = true, Advanced = true, Min = 0,
                    Help = "/dev/i2c-<bus>. The Raspberry Pi header bus is 1." },
            new() { Name = "address", Label = "I2C address", Kind = SettingKinds.Integer, Default = "0x48", HardwareOnly = true, Advanced = true, Min = 0x03, Max = 0x77,
                    Help = "The chip's 7-bit address, decimal or 0x hex. An ADS1115 answers at 0x48..0x4B depending on its ADDR pin." },
            new() { Name = "gain", Label = "Full-scale range", Kind = SettingKinds.Choice, Default = "4.096", Unit = "V", HardwareOnly = true, Advanced = true,
                    Choices = ["6.144", "4.096", "2.048", "1.024", "0.512", "0.256"],
                    Help = "The ADC's input range in volts (±). Smaller = finer resolution. It is NOT input protection: inputs must stay below the chip's supply + 0.3 V whatever the range. 4.096 suits a 3.3 V system." },
        ]
    };

    /// <summary>Every driver type this build ships, in the order a form should list them.</summary>
    public static readonly IReadOnlyList<DriverTypeSchema> Shipped = [DigitalActuator, AnalogSensor];

    /// <summary>The schema for a driver type, or null if this build does not ship it.</summary>
    public static DriverTypeSchema? Find(string driverType) =>
        Shipped.FirstOrDefault(s => string.Equals(s.DriverType, driverType, StringComparison.Ordinal));
}
