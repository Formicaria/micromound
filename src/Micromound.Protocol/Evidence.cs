using System.Text.Json.Serialization;

namespace Micromound.Protocol;

/// <summary>
/// The closed set of action outcomes — PROTOCOL.md §6.
/// <c>clamped</c> means the work happened but a limit narrowed it; <c>unverified</c> means the
/// work may have happened but nothing proves it, and the Verifier treats it as failed-until-proven.
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
}

/// <summary>Outcome of one actuation — PROTOCOL.md §6. Commands are not evidence.</summary>
public sealed class ActionRecord
{
    [JsonPropertyName("action_id")] public string ActionId { get; set; } = "";
    [JsonPropertyName("charter_id")] public string CharterId { get; set; } = "";
    [JsonPropertyName("capability")] public string Capability { get; set; } = "";
    [JsonPropertyName("parameters")] public Dictionary<string, double> Parameters { get; set; } = [];
    [JsonPropertyName("started_at")] public string StartedAt { get; set; } = "";
    [JsonPropertyName("ended_at")] public string EndedAt { get; set; } = "";
    /// <summary>succeeded | failed | clamped | refused | stopped | unverified — see <see cref="ActionOutcomes"/>.</summary>
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = ActionOutcomes.Unverified;
    /// <summary>Ids of evidence items proving the outcome. Empty ⇒ outcome is `unverified`.</summary>
    [JsonPropertyName("evidence_refs")] public List<string> EvidenceRefs { get; set; } = [];
    /// <summary>
    /// Why this outcome, in words: the limit that clamped it, the rule that refused it, the
    /// evidence that was missing. Additive field (PROTOCOL.md §10) — SAFETY.md prohibits silent
    /// failure, so every non-success outcome carries its reason on the wire.
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
