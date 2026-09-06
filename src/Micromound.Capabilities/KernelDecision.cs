using Micromound.Protocol;

namespace Micromound.Capabilities;

/// <summary>
/// A request for physical work, as a worker expresses it — ARCHITECTURE.md Layer 3.
///
/// Workers ask for <c>act.water_valve</c> for ten seconds. They do not ask for GPIO 17 HIGH, and
/// there is no field here through which they could.
/// </summary>
public sealed class CapabilityRequest
{
    /// <summary>Capability or routine id. A <c>routine.</c> prefix selects the routine path.</summary>
    public required string Capability { get; init; }

    public Dictionary<string, double> Parameters { get; init; } = [];

    /// <summary>Mission this request belongs to, recorded on the action. Empty for ad-hoc work.</summary>
    public string MissionId { get; init; } = "";

    /// <summary>Worker (ant) making the request, for the audit trail.</summary>
    public string Worker { get; init; } = "";

    /// <summary>
    /// The requesting worker's own declared ceiling from the manifest. Intersected with the
    /// charter ceiling, so a Scout Ant cannot actuate even under a charter that would allow it.
    /// </summary>
    public ActionClass? WorkerCeiling { get; init; }

    /// <summary>
    /// The submitter will observe this action's effect with a SEPARATE sensor before the record
    /// leaves the mound — a mission with a <c>verify</c> step whose <c>confirms</c> names this step.
    ///
    /// <para>It changes exactly one thing: an outcome the evidence gate would demote to
    /// <c>unverified</c> for want of any referenced evidence is held at its decided value, with
    /// "awaiting an independent observation" on the record, so the Witness still has something to
    /// confirm. Without it the verified path is unreachable for any honest actuator: a digital
    /// actuator produces no evidence (a command is not evidence), the gate demotes it the instant
    /// it runs, and <c>unverified</c> is terminal — nothing may lift it. Every other demotion still
    /// stands, the record is published only after the walk, and a mission that never made the
    /// observation demotes the record itself. Nothing claims more than something saw.</para>
    /// </summary>
    public bool ConfirmationExpected { get; init; }
}

/// <summary>
/// The kernel's answer, before anything physical happens — the pure half of the boundary.
///
/// Separating the decision from the execution is what makes the authority rules testable without
/// hardware, a simulator, or a clock: given a charter and a request, this is a function.
/// </summary>
public sealed class KernelDecision
{
    public required bool Authorized { get; init; }

    /// <summary>Null when authorized. Otherwise the specific reason — never a bare "refused".</summary>
    public RefusalReason? Refusal { get; init; }

    /// <summary>Human-readable specifics: which limit, which rule, which missing parameter.</summary>
    public string Detail { get; init; } = "";

    public IReadOnlyDictionary<string, double> RequestedParameters { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>What would actually run. Equal to the requested values when nothing narrowed them.</summary>
    public IReadOnlyDictionary<string, double> EffectiveParameters { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>True when a limit narrowed the request — the action's outcome becomes `clamped`.</summary>
    public bool Clamped { get; init; }

    /// <summary>hardware ∩ device ∩ charter, for this capability.</summary>
    public CapabilityLimits EffectiveLimits { get; init; } = new();

    /// <summary>The action class this request carries.</summary>
    public ActionClass RequiredClass { get; init; } = ActionClass.Observe;

    /// <summary>Whether the evidence policy in force demands proof for this capability.</summary>
    public bool EvidenceRequired { get; init; }

    /// <summary>The duration parameter's effective value, when the capability has one.</summary>
    public double? EffectiveDurationSeconds { get; init; }

    /// <summary>
    /// Every capability whose duty cycle and rate budget this request spends. One entry for a
    /// direct request; for a routine, the routine plus each capability it actually drives — so a
    /// routine cannot be a way around a relay's compiled cooldown.
    /// </summary>
    public IReadOnlyList<string> HistoryKeys { get; init; } = [];

    public static KernelDecision Refuse(RefusalReason reason, string detail,
        IReadOnlyDictionary<string, double> requested) => new()
    {
        Authorized = false,
        Refusal = reason,
        Detail = detail,
        RequestedParameters = requested
    };

    /// <summary>"duty_cycle: min_off_s 300 not elapsed…" — the wire form plus the specifics.</summary>
    public string DescribeRefusal() => Refusal is { } reason
        ? $"{RefusalReasons.ToWire(reason)}: {Detail}"
        : "authorized";
}
