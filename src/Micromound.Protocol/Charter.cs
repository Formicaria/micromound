using System.Text.Json.Serialization;

namespace Micromound.Protocol;

/// <summary>
/// Action classes — MICROMOUND.md "Authority model". Order matters: a charter ceiling admits
/// its own class and everything below. `Hazardous` is deliberately NOT a legal ceiling
/// (per-action authorization only, M5); the validator enforces that.
/// </summary>
public enum ActionClass
{
    Observe = 0,
    Benign = 1,
    Controlled = 2,
    Hazardous = 3
}

/// <summary>Per-capability operating limits. Charters can only narrow firmware limits.</summary>
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
/// Primary Colony inside its envelope; a mound refuses all work absent a valid charter.
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
}
