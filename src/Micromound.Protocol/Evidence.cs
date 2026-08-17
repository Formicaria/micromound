using System.Text.Json.Serialization;

namespace Micromound.Protocol;

/// <summary>
/// The closed set of action outcomes — PROTOCOL.md §6.
/// <c>clamped</c> means the work happened but a limit narrowed it; <c>unverified</c> means the
/// work may have happened but nothing proves it, and the upstream controller treats it as
/// failed-until-proven.
/// </summary>
public static class ActionOutcomes
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Clamped = "clamped";
    public const string Refused = "refused";
    public const string Stopped = "stopped";
    public const string Unverified = "unverified";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Succeeded, Failed, Clamped, Refused, Stopped, Unverified
    };

    /// <summary>
    /// Outcomes that assert physical work actually happened. Only these are evidence-gated — a
    /// refusal or a stop is a definite result, not a claim about the physical world.
    /// </summary>
    public static readonly IReadOnlySet<string> AssertPhysicalWork = new HashSet<string>(StringComparer.Ordinal)
    {
        Succeeded, Clamped
    };
}

/// <summary>
/// Outcome of one actuation — PROTOCOL.md §6. Commands are not evidence.
///
/// The record carries both what was asked for and what actually ran. Those differ whenever a
/// limit narrowed the request, and reporting only the effective value would hide the clamp from
/// the very audit trail that exists to surface it.
/// </summary>
public sealed class ActionRecord
{
    [JsonPropertyName("action_id")] public string ActionId { get; set; } = "";

    /// <summary>Mission this action ran under. Empty for actions outside a mission.</summary>
    [JsonPropertyName("mission_id")] public string MissionId { get; set; } = "";

    [JsonPropertyName("charter_id")] public string CharterId { get; set; } = "";
    [JsonPropertyName("capability")] public string Capability { get; set; } = "";

    /// <summary>Routine that invoked this action, when it came from one. Empty for a direct request.</summary>
    [JsonPropertyName("routine_id")] public string RoutineId { get; set; } = "";

    /// <summary>What the worker asked for, before any limit was applied.</summary>
    [JsonPropertyName("requested_parameters")] public Dictionary<string, double> RequestedParameters { get; set; } = [];

    /// <summary>What actually ran, after the kernel intersected every limit tier.</summary>
    [JsonPropertyName("parameters")] public Dictionary<string, double> Parameters { get; set; } = [];

    [JsonPropertyName("started_at")] public string StartedAt { get; set; } = "";
    [JsonPropertyName("ended_at")] public string EndedAt { get; set; } = "";

    /// <summary>succeeded | failed | clamped | refused | stopped | unverified — see <see cref="ActionOutcomes"/>.</summary>
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = ActionOutcomes.Unverified;

    /// <summary>
    /// Whether the governing evidence policy demanded proof for this capability. Recorded on the
    /// action rather than recomputed downstream, so a reader can tell "no evidence was required"
    /// from "evidence was required and is missing" without holding the charter.
    /// </summary>
    [JsonPropertyName("evidence_required")] public bool EvidenceRequired { get; set; }

    /// <summary>Ids of evidence items proving the outcome. Empty ⇒ outcome is `unverified`.</summary>
    [JsonPropertyName("evidence_refs")] public List<string> EvidenceRefs { get; set; } = [];

    /// <summary>
    /// Why this outcome, in words: the limit that clamped it, the rule that refused it, the
    /// evidence that was missing. SAFETY.md prohibits silent failure, so every non-success
    /// outcome carries its reason on the wire.
    /// </summary>
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
}

/// <summary>One sensor window, reading set, or content-addressed image reference.</summary>
public sealed class EvidenceItem
{
    [JsonPropertyName("evidence_id")] public string EvidenceId { get; set; } = "";
    /// <summary>sensor_window | reading | image_ref | telemetry_summary | outcome_code</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("captured_at")] public string CapturedAt { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("payload_json")] public string PayloadJson { get; set; } = "";
    /// <summary>For image_ref: sha256 content address; bytes are fetched lazily, never inlined.</summary>
    [JsonPropertyName("content_digest")] public string ContentDigest { get; set; } = "";
}

/// <summary>Batched evidence, hash-chained through the envelope stream — PROTOCOL.md §6.</summary>
public sealed class EvidenceBundle
{
    [JsonPropertyName("bundle_id")] public string BundleId { get; set; } = "";
    [JsonPropertyName("items")] public List<EvidenceItem> Items { get; set; } = [];
    /// <summary>Set when storage exhaustion forced eviction of acked items — itself reported.</summary>
    [JsonPropertyName("evicted_acked_items")] public int EvictedAckedItems { get; set; }
}
