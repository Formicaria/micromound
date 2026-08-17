using System.Text.Json.Serialization;

namespace Micromound.Protocol;

/// <summary>
/// What a mission step does — PROTOCOL.md §9. A closed set, because the whole point of a
/// structured work packet is that it executes identically without a language model: a runtime
/// that meets an op it does not know refuses the mission rather than improvising.
/// </summary>
public static class MissionStepOps
{
    /// <summary>Read a capability and bind the reading to the step id.</summary>
    public const string Sense = "sense";
    /// <summary>Request an actuation through the capability kernel.</summary>
    public const string Act = "act";
    /// <summary>Invoke a registered, charter-enabled routine.</summary>
    public const string Routine = "routine";
    /// <summary>Re-read and compare against an earlier step, producing evidence.</summary>
    public const string Verify = "verify";
    /// <summary>Emit the structured mission report.</summary>
    public const string Report = "report";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Sense, Act, Routine, Verify, Report
    };
}

/// <summary>Comparison operators a deterministic condition may use. No expression language.</summary>
public static class ConditionOps
{
    public const string LessThan = "lt";
    public const string LessOrEqual = "lte";
    public const string GreaterThan = "gt";
    public const string GreaterOrEqual = "gte";
    public const string Equal = "eq";
    public const string NotEqual = "neq";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        LessThan, LessOrEqual, GreaterThan, GreaterOrEqual, Equal, NotEqual
    };

    /// <summary>
    /// Evaluate one comparison. Returns false for an unknown operator rather than throwing —
    /// an unrecognised condition must never read as "condition met" (MICROMOUND.md: ambiguity
    /// resolves downward). Callers that need to distinguish "false" from "unknown" check
    /// <see cref="All"/> first, which is what mission validation does.
    /// </summary>
    public static bool Evaluate(double left, string op, double right) => op switch
    {
        LessThan => left < right,
        LessOrEqual => left <= right,
        GreaterThan => left > right,
        GreaterOrEqual => left >= right,
        Equal => left == right,
        NotEqual => left != right,
        _ => false
    };
}

/// <summary>
/// A deterministic guard on a step: compare an earlier step's reading against a constant.
/// Deliberately not an expression language — one source, one operator, one number.
/// </summary>
public sealed class StepCondition
{
    /// <summary>Step id whose sensed value is the left-hand side.</summary>
    [JsonPropertyName("source_step")] public string SourceStep { get; set; } = "";
    /// <summary>lt | lte | gt | gte | eq | neq — see <see cref="ConditionOps"/>.</summary>
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("value")] public double Value { get; set; }
}

/// <summary>One ordered step of a mission — PROTOCOL.md §9.</summary>
public sealed class MissionStep
{
    [JsonPropertyName("step_id")] public string StepId { get; set; } = "";
    /// <summary>sense | act | routine | verify | report — see <see cref="MissionStepOps"/>.</summary>
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    /// <summary>Capability this step reads or actuates. Empty for `report`.</summary>
    [JsonPropertyName("capability")] public string Capability { get; set; } = "";
    /// <summary>Routine id for a `routine` step. Empty otherwise.</summary>
    [JsonPropertyName("routine_id")] public string RoutineId { get; set; } = "";
    [JsonPropertyName("parameters")] public Dictionary<string, double> Parameters { get; set; } = [];
    /// <summary>When set, the step runs only if the condition holds. Null ⇒ unconditional.</summary>
    [JsonPropertyName("condition")] public StepCondition? Condition { get; set; }
    /// <summary>
    /// Label this step's evidence is filed under (e.g. "soil_before", "soil_after"), so the
    /// mission's evidence requirements can name what it expects without knowing step ids.
    /// </summary>
    [JsonPropertyName("evidence_tag")] public string EvidenceTag { get; set; } = "";
}

/// <summary>
/// A structured work packet — PROTOCOL.md §9. The authoritative execution representation, and it
/// stays executable with no language model in the loop: ordered steps, deterministic conditions,
/// enumerated capabilities and routines.
///
/// <see cref="Context"/> is the one free-text field, and it is advisory only. Nothing in the
/// runtime may branch on it; it exists so a human (or an optional reasoner) reading the mission
/// knows what it is for.
/// </summary>
public sealed class Mission
{
    [JsonPropertyName("mission_id")] public string MissionId { get; set; } = "";
    [JsonPropertyName("mound_id")] public string MoundId { get; set; } = "";
    /// <summary>Charter this mission executes under. A mission never carries its own authority.</summary>
    [JsonPropertyName("charter_id")] public string CharterId { get; set; } = "";
    /// <summary>Worker (ant) the mound should dispatch this to, by name. Empty ⇒ Mound Major's choice.</summary>
    [JsonPropertyName("worker")] public string Worker { get; set; } = "";
    [JsonPropertyName("required_capabilities")] public List<string> RequiredCapabilities { get; set; } = [];
    [JsonPropertyName("allowed_routines")] public List<string> AllowedRoutines { get; set; } = [];
    [JsonPropertyName("steps")] public List<MissionStep> Steps { get; set; } = [];
    /// <summary>Evidence tags this mission must produce for its report to count as verified.</summary>
    [JsonPropertyName("required_evidence")] public List<string> RequiredEvidence { get; set; } = [];
    [JsonPropertyName("safe_state")] public string SafeState { get; set; } = "";
    [JsonPropertyName("expires_at")] public string ExpiresAt { get; set; } = "";
    /// <summary>Human-readable context. Advisory only — no runtime path may branch on this.</summary>
    [JsonPropertyName("context")] public string Context { get; set; } = "";
}

/// <summary>Closed set of mission end states.</summary>
public static class MissionStates
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Refused = "refused";
    public const string Stopped = "stopped";
    public const string Quiesced = "quiesced";
    /// <summary>Ran to the end, but at least one required evidence tag never resolved.</summary>
    public const string Unverified = "unverified";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Completed, Failed, Refused, Stopped, Quiesced, Unverified
    };
}

/// <summary>Closed set of per-step outcomes.</summary>
public static class MissionStepStates
{
    public const string Executed = "executed";
    /// <summary>The step's condition did not hold. Not a failure.</summary>
    public const string Skipped = "skipped";
    public const string Refused = "refused";
    public const string Failed = "failed";
    public const string Stopped = "stopped";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Executed, Skipped, Refused, Failed, Stopped
    };
}

public sealed class MissionStepResult
{
    [JsonPropertyName("step_id")] public string StepId { get; set; } = "";
    /// <summary>executed | skipped | refused | failed | stopped — see <see cref="MissionStepStates"/>.</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "";
    /// <summary>Sensed value for a `sense` or `verify` step. Null when the step read nothing.</summary>
    [JsonPropertyName("value")] public double? Value { get; set; }
    /// <summary>Action record this step produced, if it actuated.</summary>
    [JsonPropertyName("action_id")] public string ActionId { get; set; } = "";
    [JsonPropertyName("evidence_refs")] public List<string> EvidenceRefs { get; set; } = [];
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
}

/// <summary>The structured outcome a mound reports back for one mission — PROTOCOL.md §9.</summary>
public sealed class MissionReport
{
    [JsonPropertyName("mission_id")] public string MissionId { get; set; } = "";
    [JsonPropertyName("charter_id")] public string CharterId { get; set; } = "";
    /// <summary>completed | failed | refused | stopped | quiesced | unverified.</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("started_at")] public string StartedAt { get; set; } = "";
    [JsonPropertyName("ended_at")] public string EndedAt { get; set; } = "";
    [JsonPropertyName("steps")] public List<MissionStepResult> Steps { get; set; } = [];
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
}
