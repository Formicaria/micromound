using System.Text.Json.Serialization;

namespace Micromound.Protocol;

/// <summary>Reasoning modes — ARCHITECTURE.md "Optional reasoning". Default is <c>none</c>.</summary>
public static class ReasoningModes
{
    /// <summary>Deterministic workflows, rules, and routines only. The default, and the only mode a standard mound needs.</summary>
    public const string None = "none";
    /// <summary>Ask a configured upstream reasoning service. Requires connectivity by definition.</summary>
    public const string Remote = "remote";
    /// <summary>A lightweight local model on capable hardware, for ambiguous or disconnected work.</summary>
    public const string Local = "local";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        None, Remote, Local
    };
}

/// <summary>How a worker behaves when the mound loses its uplink.</summary>
public static class OfflineBehaviours
{
    /// <summary>Keep running within already-issued authority until the lease expires.</summary>
    public const string Continue = "continue";
    /// <summary>Finish in-progress work, then stop taking new work.</summary>
    public const string Drain = "drain";
    /// <summary>Stop immediately on loss of uplink, regardless of remaining lease.</summary>
    public const string Suspend = "suspend";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Continue, Drain, Suspend
    };
}

/// <summary>
/// One physical device bound to a driver. <see cref="Settings"/> values are strings on purpose:
/// the manifest decoder stays one fixed shape, and each driver parses and validates its own
/// settings (a pin number, a bus address, a channel) where the knowledge of what is legal lives.
/// </summary>
/// <summary>
/// What kind of thing a declared worker is — CONFIGURATION.md. Closed, because "ant does not mean
/// language model" (MICROMOUND.md design rule 9) is only enforceable if a manifest cannot invent a
/// kind whose meaning nothing agrees on. <c>reasoning</c> is the only value that implies a model,
/// and even that one only proposes.
/// </summary>
public static class RuntimeTypes
{
    public const string Deterministic = "deterministic";
    public const string Algorithmic = "algorithmic";
    public const string Sensor = "sensor";
    public const string Actuator = "actuator";
    public const string Reasoning = "reasoning";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Deterministic, Algorithmic, Sensor, Actuator, Reasoning
    };
}

public sealed class HardwareBinding
{
    [JsonPropertyName("driver")] public string Driver { get; set; } = "";
    [JsonPropertyName("settings")] public Dictionary<string, string> Settings { get; set; } = [];
}

/// <summary>
/// A specialized ant, declared rather than coded — ANTS.md. Deployments add application workers
/// (Soil Ant, Drive Ant, Vision Inspection Ant) on top of the six default ants without changing
/// the runtime.
/// </summary>
public sealed class WorkerDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("purpose")] public string Purpose { get; set; } = "";
    /// <summary>deterministic | algorithmic | sensor | actuator | reasoning — see <see cref="RuntimeTypes"/>.</summary>
    [JsonPropertyName("runtime_type")] public string RuntimeType { get; set; } = RuntimeTypes.Deterministic;
    /// <summary>Capabilities this worker reads or requests.</summary>
    [JsonPropertyName("consumes")] public List<string> Consumes { get; set; } = [];
    /// <summary>Capabilities this worker offers to other workers.</summary>
    [JsonPropertyName("exposes")] public List<string> Exposes { get; set; } = [];
    /// <summary>Highest action class this worker may ever request. Intersected with the charter ceiling.</summary>
    [JsonPropertyName("action_ceiling")] public string ActionCeiling { get; set; } = "observe";
    [JsonPropertyName("required_evidence")] public List<string> RequiredEvidence { get; set; } = [];
    /// <summary>continue | drain | suspend — see <see cref="OfflineBehaviours"/>.</summary>
    [JsonPropertyName("offline_behaviour")] public string OfflineBehaviour { get; set; } = OfflineBehaviours.Continue;
    /// <summary>True when this worker cannot function without a reasoning provider.</summary>
    [JsonPropertyName("requires_reasoning")] public bool RequiresReasoning { get; set; }
}

public sealed class ReasoningConfig
{
    /// <summary>none | remote | local — see <see cref="ReasoningModes"/>. Defaults to none.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = ReasoningModes.None;
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
}

/// <summary>
/// The mound's declarative configuration — CONFIGURATION.md. Signed and delivered by the upstream
/// controller as a <c>config</c> envelope, or loaded from disk for a standalone mound.
///
/// This is the middle limit tier: <see cref="DeviceLimits"/> sits between what the hardware can
/// physically do and what a charter grants, so an operator can narrow a device below its hardware
/// ceiling permanently, independent of whatever any mission later asks for.
///
/// Configuration is validated before activation and fails closed — an unparseable or internally
/// inconsistent manifest leaves the previous manifest in force and is reported.
/// </summary>
public sealed class MoundManifest
{
    [JsonPropertyName("manifest_id")] public string ManifestId { get; set; } = "";
    [JsonPropertyName("mound_id")] public string MoundId { get; set; } = "";
    [JsonPropertyName("issued_at")] public string IssuedAt { get; set; } = "";
    /// <summary>Logical device name → driver binding, e.g. "irrigation" → gpio_relay pin 17.</summary>
    [JsonPropertyName("hardware")] public Dictionary<string, HardwareBinding> Hardware { get; set; } = [];
    /// <summary>Capabilities this mound declares it physically has.</summary>
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = [];
    /// <summary>Routine ids available on this mound. A charter may enable a subset, never more.</summary>
    [JsonPropertyName("routines")] public List<string> Routines { get; set; } = [];
    [JsonPropertyName("workers")] public List<WorkerDefinition> Workers { get; set; } = [];
    /// <summary>Operator-set limits, narrower than hardware and independent of any charter.</summary>
    [JsonPropertyName("device_limits")] public Dictionary<string, CapabilityLimits> DeviceLimits { get; set; } = [];
    [JsonPropertyName("reasoning")] public ReasoningConfig Reasoning { get; set; } = new();
    /// <summary>The state this mound de-energizes into. Every charter must be compatible with it.</summary>
    [JsonPropertyName("safe_state")] public string SafeState { get; set; } = "all_actuators_off";
}
