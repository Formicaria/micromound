using Micromound.Protocol;

namespace Micromound.Capabilities;

/// <summary>An inclusive numeric range a parameter must land in. Requests are clamped, not refused.</summary>
public readonly record struct ParameterRange(double Min, double Max)
{
    public bool Contains(double value) => value >= Min && value <= Max;

    public double Clamp(double value) => value < Min ? Min : value > Max ? Max : value;
}

/// <summary>
/// What a capability IS on this device — CAPABILITIES.md. Registered once at startup from the
/// hardware manifest, never mutated by a charter, a mission, or a model.
///
/// <see cref="HardwareLimits"/> is the innermost limit tier: the bound the physical device
/// imposes. Nothing above it can widen it, which is why it is registered here alongside the
/// driver rather than carried in any document that arrives over the network.
/// </summary>
public sealed class CapabilityDescriptor
{
    public required string Id { get; init; }

    /// <summary>
    /// The action class every request for this capability carries. A <c>sense.</c> capability is
    /// always <see cref="ActionClass.Observe"/> — the registry enforces that, because a sensor
    /// that has been classified as actuation is a configuration error that would otherwise only
    /// surface as an unexplained refusal.
    /// </summary>
    public ActionClass Class { get; init; } = ActionClass.Observe;

    public string Description { get; init; } = "";

    /// <summary>The device's own bound. Innermost tier; not remotely settable.</summary>
    public CapabilityLimits HardwareLimits { get; init; } = new();

    /// <summary>Parameter names this capability accepts. Anything else is refused, never ignored.</summary>
    public IReadOnlySet<string> Parameters { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Parameters that must be supplied. Must be a subset of <see cref="Parameters"/>.</summary>
    public IReadOnlySet<string> RequiredParameters { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Which parameter <c>max_on_s</c>, <c>min_off_s</c>, and <c>max_rate_per_h</c> govern —
    /// conventionally "on_s". Null when the capability has no duration (a momentary read).
    /// </summary>
    public string? DurationParameter { get; init; }

    /// <summary>
    /// Which parameter <c>min</c>/<c>max</c> govern — a servo angle, a motor speed, a position
    /// along a geofenced axis. Null when the capability has no continuous magnitude.
    /// </summary>
    public string? MagnitudeParameter { get; init; }

    /// <summary>Per-parameter hard ranges from the driver, applied under the limit tiers.</summary>
    public IReadOnlyDictionary<string, ParameterRange> ParameterRanges { get; init; } =
        new Dictionary<string, ParameterRange>(StringComparer.Ordinal);

    /// <summary>
    /// Whether the underlying device is currently usable. Set by the driver's health reporting;
    /// a faulted device refuses rather than silently doing nothing.
    /// </summary>
    public bool Available { get; init; } = true;
}

/// <summary>
/// A named deterministic local behaviour — ARCHITECTURE.md "Routines". Routines exist so an
/// upstream controller can delegate useful physical work ("run a water cycle") without
/// micromanaging every hardware transition across a link that may drop mid-sequence.
///
/// A charter can enable a routine and narrow its parameters. It can never register one, and it
/// can never widen <see cref="HardwareLimits"/> or <see cref="ParameterRanges"/> — on a
/// constrained controller those are compiled into the firmware image.
/// </summary>
public sealed class RoutineDescriptor
{
    public required string Id { get; init; }

    public ActionClass Class { get; init; } = ActionClass.Benign;

    public string Description { get; init; } = "";

    /// <summary>
    /// Capabilities this routine drives. All must be registered and available. They need NOT be
    /// separately granted in a charter's `capabilities` list — the routine is the unit of
    /// delegation, which is the whole point of having routines.
    ///
    /// They are not exempt from limits, though: the kernel intersects each backing capability's
    /// hardware bound into the routine's, and spends each one's duty-cycle and rate budget.
    /// </summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    /// <summary>
    /// The routine's own compiled bound. Intersected with every backing capability's hardware
    /// bound before use, so a generously declared routine cannot loosen the relay underneath it.
    /// </summary>
    public CapabilityLimits HardwareLimits { get; init; } = new();

    public IReadOnlySet<string> Parameters { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> RequiredParameters { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ParameterRange> ParameterRanges { get; init; } =
        new Dictionary<string, ParameterRange>(StringComparer.Ordinal);

    public string? DurationParameter { get; init; }

    public string? MagnitudeParameter { get; init; }

    /// <summary>Evidence tags this routine is expected to produce, for the Witness Ant to correlate.</summary>
    public IReadOnlyList<string> EvidenceExpectations { get; init; } = [];

    /// <summary>Whether the routine can be interrupted cleanly mid-sequence.</summary>
    public bool Cancellable { get; init; } = true;

    /// <summary>The state this routine leaves the hardware in when cancelled or stopped.</summary>
    public string SafeState { get; init; } = "all_actuators_off";

    public bool Available { get; init; } = true;
}
