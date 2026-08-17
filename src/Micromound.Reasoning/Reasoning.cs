using Micromound.Protocol;

namespace Micromound.Reasoning;

/// <summary>
/// A question a deterministic workflow could not answer on its own. Deliberately narrow: a
/// reasoner is asked to choose among options that are already authorized, or to interpret an
/// observation. It is never asked what to do.
/// </summary>
public sealed class ReasoningQuery
{
    public required string Question { get; init; }

    /// <summary>
    /// The only answers that will be accepted. Empty means the caller wants an interpretation
    /// (a scene classification, a summary), not a decision.
    /// </summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>Observations the reasoner may consider — readings, images, telemetry summaries.</summary>
    public IReadOnlyList<EvidenceItem> Observations { get; init; } = [];

    public string MissionId { get; init; } = "";
    public string Context { get; init; } = "";
}

/// <summary>
/// What comes back. The name is the contract: this is a PROPOSAL.
///
/// Nothing here is an instruction. The caller may discard it, and whatever it chooses to do with
/// it still arrives at the capability kernel as an ordinary request, carrying no extra authority
/// for having been suggested by a model.
/// </summary>
public sealed class ReasoningProposal
{
    public required bool Answered { get; init; }

    /// <summary>The chosen option, which the caller must check is still one it offered.</summary>
    public string Choice { get; init; } = "";

    public string Rationale { get; init; } = "";

    /// <summary>Provider's self-reported confidence, advisory only. No threshold grants authority.</summary>
    public double? Confidence { get; init; }

    public string Detail { get; init; } = "";

    public static ReasoningProposal None(string detail) => new() { Answered = false, Detail = detail };

    public static ReasoningProposal Choose(string choice, string rationale = "", double? confidence = null) =>
        new() { Answered = true, Choice = choice, Rationale = rationale, Confidence = confidence };
}

/// <summary>
/// The optional reasoning seam — ARCHITECTURE.md "Optional reasoning". Modes are
/// <c>none</c> (default), <c>remote</c>, and <c>local</c>.
///
/// Note what this project does NOT reference: Micromound.Capabilities. That is not an oversight
/// and not a convention — it is the enforcement. A reasoning provider cannot call the capability
/// kernel, hold an executor, or touch a driver, because the project reference that would let it
/// does not exist and adding one would be a visible change to a .csproj rather than a line of
/// code inside a method.
///
/// Consequently a model on this mound cannot extend a lease, raise an action class, disable a
/// stop, widen a limit, or self-authorize anything. It can answer a question.
/// </summary>
public interface IReasoningProvider
{
    /// <summary>none | remote | local — see <see cref="ReasoningModes"/>.</summary>
    string Mode { get; }

    /// <summary>False when the provider cannot be reached — remote mode while offline, say.</summary>
    bool IsAvailable { get; }

    ReasoningProposal Ask(ReasoningQuery query, DateTimeOffset now);
}

/// <summary>
/// The default provider, and the one a standard mound ships with: it answers nothing.
///
/// It exists so that "reasoning is optional" is a runtime fact rather than a set of null checks
/// scattered through the Mound Major. A workflow that cannot proceed without an answer gets a
/// definite "no answer" here and fails deterministically, which is the correct outcome for a
/// mound configured with <c>reasoning.mode: none</c>.
/// </summary>
public sealed class NoReasoningProvider : IReasoningProvider
{
    public string Mode => ReasoningModes.None;

    public bool IsAvailable => false;

    public ReasoningProposal Ask(ReasoningQuery query, DateTimeOffset now) =>
        ReasoningProposal.None("reasoning.mode is 'none'; this mound answers deterministically or not at all");
}

/// <summary>
/// Guard rails a caller applies to any proposal before acting on it. Kept here so that every
/// call site enforces the same thing, and so the rules are visible next to the interface they
/// constrain rather than buried in the coordinator.
/// </summary>
public static class ProposalGuard
{
    /// <summary>
    /// A proposal is usable only if it answered, and — when the query offered a closed option
    /// set — chose one of exactly those options. A provider that invents an option has produced
    /// a string, not a decision.
    /// </summary>
    public static bool IsUsable(ReasoningQuery query, ReasoningProposal proposal, out string reason)
    {
        reason = "";

        if (!proposal.Answered)
        {
            reason = string.IsNullOrEmpty(proposal.Detail) ? "provider returned no answer" : proposal.Detail;
            return false;
        }

        if (query.Options.Count == 0) return true;

        if (!query.Options.Contains(proposal.Choice, StringComparer.Ordinal))
        {
            reason = $"provider chose '{proposal.Choice}', which was not among the offered options";
            return false;
        }

        return true;
    }
}
