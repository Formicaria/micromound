using System.Text.Json.Serialization;

namespace Micromound.Protocol;

/// <summary>
/// Action classes — MICROMOUND.md "Authority model". Order matters: a charter ceiling admits
/// its own class and everything below. `Hazardous` is deliberately NOT a legal ceiling
/// (per-action authorization only); the validator enforces that.
/// </summary>
public enum ActionClass
{
    Observe = 0,
    Benign = 1,
    Controlled = 2,
    Hazardous = 3
}

/// <summary>
/// Operating limits for one capability or routine.
///
/// The same shape is used at all three tiers the kernel intersects — hardware/firmware, device
/// manifest, and charter (SAFETY.md Layer 1). Using one type for all three is deliberate: it
/// makes "the narrowest bound wins" a single function rather than three near-duplicate ones, and
/// it means a constrained controller needs exactly one decoder.
///
/// Geofence and workspace bounds are expressed as <see cref="Min"/>/<see cref="Max"/> on the
/// relevant positional capability; richer workspace shapes are a later protocol addition, not an
/// ad-hoc field here.
/// </summary>
public sealed class CapabilityLimits
{
    [JsonPropertyName("max_on_s")] public double? MaxOnSeconds { get; set; }
    [JsonPropertyName("min_off_s")] public double? MinOffSeconds { get; set; }
    [JsonPropertyName("min")] public double? Min { get; set; }
    [JsonPropertyName("max")] public double? Max { get; set; }
    [JsonPropertyName("max_rate_per_h")] public double? MaxRatePerHour { get; set; }
}

public sealed class EvidencePolicy
{
    /// <summary>Capability patterns whose use requires evidence (e.g. "act.*", "routine.*").</summary>
    [JsonPropertyName("required_for")] public List<string> RequiredFor { get; set; } = [];
    [JsonPropertyName("min_interval_s")] public int MinIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// The delegation document — PROTOCOL.md §4. Complete replacement, never a diff. Signed by the
/// upstream controller inside its envelope; a mound refuses all work absent a valid charter
/// covering the requested operation.
/// </summary>
public sealed class Charter
{
    [JsonPropertyName("charter_id")] public string CharterId { get; set; } = "";
    [JsonPropertyName("mound_id")] public string MoundId { get; set; } = "";
    [JsonPropertyName("mission_ref")] public string MissionRef { get; set; } = "";
    [JsonPropertyName("issued_at")] public string IssuedAt { get; set; } = "";
    [JsonPropertyName("expires_at")] public string ExpiresAt { get; set; } = "";
    [JsonPropertyName("lease_ttl_s")] public int LeaseTtlSeconds { get; set; }
    [JsonPropertyName("action_ceiling")] public string ActionCeiling { get; set; } = "observe";
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = [];

    /// <summary>
    /// Routine ids this charter enables — PROTOCOL.md §4. A routine is invocable only when it
    /// appears here AND the runtime (or firmware build) actually registers it: the charter
    /// selects from behaviour that already exists, it never defines new behaviour. Routine
    /// parameters still clamp to the registered routine's compiled ranges.
    /// </summary>
    [JsonPropertyName("routines")] public List<string> Routines { get; set; } = [];

    /// <summary>
    /// Per-capability and per-routine limits. This is the outermost of the three tiers the
    /// capability kernel intersects; it can only narrow the device manifest and the hardware
    /// limits beneath it.
    /// </summary>
    [JsonPropertyName("limits")] public Dictionary<string, CapabilityLimits> Limits { get; set; } = [];

    [JsonPropertyName("evidence")] public EvidencePolicy Evidence { get; set; } = new();
    [JsonPropertyName("safe_state")] public string SafeState { get; set; } = "all_actuators_off";
    [JsonPropertyName("sync_interval_s")] public int SyncIntervalSeconds { get; set; } = 15;
}

public static class ActionClasses
{
    public static bool TryParse(string value, out ActionClass parsed)
    {
        switch (value)
        {
            case "observe": parsed = ActionClass.Observe; return true;
            case "benign": parsed = ActionClass.Benign; return true;
            case "controlled": parsed = ActionClass.Controlled; return true;
            case "hazardous": parsed = ActionClass.Hazardous; return true;
            default: parsed = ActionClass.Observe; return false;
        }
    }

    public static string ToWire(ActionClass value) => value switch
    {
        ActionClass.Observe => "observe",
        ActionClass.Benign => "benign",
        ActionClass.Controlled => "controlled",
        ActionClass.Hazardous => "hazardous",
        _ => "observe"
    };
}
