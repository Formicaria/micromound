using System.Globalization;
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

/// <summary>
/// What a <c>verify</c> step asserts it will observe — the postcondition, PROTOCOL.md §9
/// (`v0.9.30`).
///
/// <para><b>Why this exists, and what was wrong without it.</b> Until this, a `verify` step
/// established only that <i>some</i> independent observation existed, was fresh, and was captured
/// after the action began. Nothing compared what it saw against what the action was supposed to
/// achieve — so a limit switch reporting "open" after a close command confirmed the close. The
/// mound reported `succeeded`, the controller had a signed record saying so, and every layer in
/// between was honest about a fact nobody had actually checked. "The second sense is not
/// redundancy" needs a claim to test the second sense against, and this is that claim.</para>
///
/// <para>Deliberately the same shape as <see cref="StepCondition"/>: one operator, one number, no
/// expression language. A condition asks whether a step should run; an expectation asks whether the
/// step before it worked. They are different questions about the same kind of comparison.</para>
/// </summary>
public sealed class StepExpectation
{
    /// <summary>lt | lte | gt | gte | eq | neq — see <see cref="ConditionOps"/>.</summary>
    [JsonPropertyName("op")] public string Op { get; set; } = "";

    /// <summary>The value the observation is compared against.</summary>
    [JsonPropertyName("value")] public double Value { get; set; }

    /// <summary>
    /// Slack allowed on an <c>eq</c> or <c>neq</c> comparison: the observation satisfies <c>eq</c>
    /// when it is within this much of <see cref="Value"/>. Zero — the default — is exact, which is
    /// what a boolean line wants and what an analog reading almost never does. Ignored by the
    /// ordering operators, where a threshold already expresses the slack.
    /// </summary>
    [JsonPropertyName("tolerance")] public double Tolerance { get; set; }

    /// <summary>
    /// The unit the author believed they were asserting in (`pct`, `V`, `closed`). Advisory and
    /// recorded in the refusal text, so an expectation written against the wrong scale is legible
    /// afterwards. It is NOT converted — a mound does not silently reinterpret a number.
    /// </summary>
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";

    /// <summary>Does an observed value satisfy this expectation?</summary>
    public bool IsMet(double observed) => Op switch
    {
        ConditionOps.Equal => Math.Abs(observed - Value) <= Math.Abs(Tolerance),
        ConditionOps.NotEqual => Math.Abs(observed - Value) > Math.Abs(Tolerance),
        _ => ConditionOps.Evaluate(observed, Op, Value)
    };

    /// <summary>How this expectation reads in a refusal, e.g. <c>eq 1 ±0.5 pct</c>.</summary>
    public string Describe() =>
        $"{Op} {Value.ToString(CultureInfo.InvariantCulture)}" +
        (Tolerance != 0 ? $" ±{Math.Abs(Tolerance).ToString(CultureInfo.InvariantCulture)}" : "") +
        (string.IsNullOrEmpty(Unit) ? "" : $" {Unit}");
}

/// <summary>
/// Named protocol semantics a mission may REQUIRE the runtime to implement — PROTOCOL.md §9.
///
/// <para>The set is closed and additive: a name is added here when a change alters what a mission
/// MEANS in a way an older runtime would silently ignore rather than reject. A runtime that does not
/// recognise a required name refuses the mission whole, before any step runs, which turns a silent
/// semantic mismatch into a loud validation refusal.</para>
///
/// <para>A device advertises the set it supports at enrollment (PROTOCOL.md §3, <c>features</c>), so
/// a controller can tell before it sends. Both halves are needed: the advertisement lets a controller
/// avoid the mistake, and the requirement catches it when the controller gets it wrong anyway.</para>
/// </summary>
public static class ProtocolFeatures
{
    /// <summary>
    /// A <c>verify</c> step's <c>expect</c> is evaluated: the confirming observation's VALUE is
    /// compared against the assertion, and an action whose confirmation disagrees degrades to
    /// `unverified`. Added `v0.9.30`. Without it a runtime confirms on presence alone.
    /// </summary>
    public const string Postconditions = "postconditions";

    /// <summary>Everything this build implements. What a device advertises, and what it validates against.</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Postconditions
    };
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

    /// <summary>
    /// The earlier step whose action this one confirms. Only meaningful on a <c>verify</c> step,
    /// and it is what makes `verify` different from `sense` at all.
    ///
    /// ARCHITECTURE.md: "the second sense is not redundancy… the first reading justifies the
    /// action, the second is independent evidence of its effect, and without it the outcome is
    /// `unverified` no matter what the driver returned." That sentence needs a link between the
    /// confirming observation and the action being confirmed, and this is it. Naming the step
    /// explicitly, rather than inferring the pairing from capability names, keeps missions the
    /// deterministic packets §9 says they are: one source, named, no matching rules to learn.
    /// </summary>
    [JsonPropertyName("confirms")] public string Confirms { get; set; } = "";

    /// <summary>
    /// Seconds to wait BEFORE this step runs, so the physical world can catch up with the step before
    /// it. Zero — the default — means run immediately.
    ///
    /// <para><b>Why the protocol needs this at all.</b> Nothing physical is instantaneous. A solenoid
    /// takes tens of milliseconds; a motorised ball valve takes seconds. A <c>verify</c> step that
    /// reads its limit switch in the same instant the <c>act</c> step energised the coil reads the
    /// world before the actuator moved, and reports honestly that nothing confirmed the actuation — so
    /// a mound with perfectly good hardware could never reach <c>verified</c> at all. The settle
    /// window is the missing half of "act, then confirm": it is how a mission states the travel time
    /// of the thing it is confirming.</para>
    ///
    /// <para><b>Why it is bounded at <see cref="MissionLimits.MaxSettleSeconds"/>.</b> The wait happens
    /// on the mission's own thread, inside the service tick, so it delays the heartbeat, the hold
    /// release and the watchdog kick for exactly as long as it lasts. The bound keeps any settle far
    /// inside the daemon's default 30 s heartbeat timeout, so a mission can never talk the runtime
    /// into looking dead. Something that takes longer than that to move is not one mission with a long
    /// pause in the middle — it is two missions, and the controller schedules the second.</para>
    ///
    /// <para>Only <c>sense</c> and <c>verify</c> steps may carry one: a wait before acting is just a
    /// mission that starts later. A settle on a step whose condition did not hold, or that a halt
    /// suppressed, is not waited out — there is nothing to see.</para>
    /// </summary>
    [JsonPropertyName("settle_s")] public double SettleSeconds { get; set; }

    /// <summary>
    /// What this step asserts it will observe — see <see cref="StepExpectation"/>. Only meaningful
    /// on a <c>verify</c> step, where it is the difference between "something independent looked"
    /// and "what it saw agrees". Null — the default — keeps the pre-`v0.9.30` behaviour: presence,
    /// freshness and ordering are checked, and the value is not.
    ///
    /// <para>A mission that carries one should also name <see cref="ProtocolFeatures.Postconditions"/>
    /// in <see cref="Mission.RequiredFeatures"/>, so a runtime too old to evaluate it refuses the
    /// mission instead of silently confirming on presence alone.</para>
    /// </summary>
    [JsonPropertyName("expect")] public StepExpectation? Expect { get; set; }
}

/// <summary>Numeric bounds a mission is validated against — one place, so validator and docs agree.</summary>
public static class MissionLimits
{
    /// <summary>
    /// The longest <see cref="MissionStep.SettleSeconds"/> a step may ask for. Well inside the daemon's
    /// default 30 s guard heartbeat timeout (<c>--heartbeat-s</c>), because the wait blocks the tick.
    /// </summary>
    public const double MaxSettleSeconds = 10;
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
    /// <summary>
    /// Protocol features this mission needs the runtime to actually implement — see
    /// <see cref="ProtocolFeatures"/>. A mound that does not recognise every name here refuses the
    /// mission whole, before any step runs.
    ///
    /// <para><b>Why a list of names rather than a version number.</b> The failure this prevents is
    /// silent: a semantic addition that an older runtime ignores rather than rejects. A step's
    /// <c>expect</c> is exactly that shape — an old mound skips the unknown member, confirms on
    /// presence alone, and reports a success the mission's author would have called unverified.
    /// Naming the requirement makes the mismatch loud at validation, where it costs nothing,
    /// instead of silent at the point a valve did the wrong thing.</para>
    ///
    /// <para>It cannot retrofit a refusal into runtimes that already shipped: a mound older than
    /// `v0.9.30` ignores this field as it ignores <c>expect</c>. That is what the device's
    /// advertised feature list at enrollment is for (PROTOCOL.md §3) — the controller checks what a
    /// device supports before sending, and this field catches the rest.</para>
    /// </summary>
    [JsonPropertyName("required_features")] public List<string> RequiredFeatures { get; set; } = [];
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
