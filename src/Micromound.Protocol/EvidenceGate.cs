namespace Micromound.Protocol;

/// <summary>
/// "Commands are not evidence" (MICROMOUND.md core principle 6), enforced as a pure function.
///
/// An action that claims physical work happened keeps its optimistic outcome only if the evidence
/// it references actually resolves, parses, and is fresh. Everything else is <c>unverified</c> —
/// never silently assumed done.
///
/// This lives in Micromound.Protocol rather than Micromound.Evidence deliberately: it is a rule
/// about what a contract means, it does no I/O, and the ESP32 mirror needs the same rule in C.
/// Micromound.Evidence owns the stateful machinery around it — capture, correlation, the local
/// store, the pending-sync queue — and calls this to decide the verdict.
/// </summary>
public static class EvidenceGate
{
    public static bool RequiresEvidence(EvidencePolicy policy, string capability) =>
        CapabilityPattern.MatchesAny(policy.RequiredFor, capability);

    /// <summary>
    /// Returns the outcome the record is entitled to. Only outcomes that assert work happened
    /// (<c>succeeded</c>, <c>clamped</c>) are gated — a refusal or a stop is a definite result
    /// and needs no proof.
    /// </summary>
    public static string Gate(
        ActionRecord record,
        EvidencePolicy policy,
        IReadOnlyDictionary<string, EvidenceItem> evidenceById,
        DateTimeOffset now,
        out string reason)
    {
        reason = "";

        if (!ActionOutcomes.AssertPhysicalWork.Contains(record.Outcome))
            return record.Outcome;

        if (record.EvidenceRefs.Count == 0)
        {
            reason = "no evidence referenced";
            return ActionOutcomes.Unverified;
        }

        if (!ProtocolTime.TryParse(record.StartedAt, out var started))
            started = now;

        var required = RequiresEvidence(policy, record.Capability);
        var oldestAcceptable = started.AddSeconds(-Math.Max(policy.MinIntervalSeconds, 0));

        foreach (var id in record.EvidenceRefs)
        {
            if (!evidenceById.TryGetValue(id, out var item))
            {
                reason = $"evidence '{id}' is missing";
                return ActionOutcomes.Unverified;
            }

            if (!ProtocolTime.TryParse(item.CapturedAt, out var captured))
            {
                reason = $"evidence '{id}' has an unparseable captured_at";
                return ActionOutcomes.Unverified;
            }

            if (captured > now)
            {
                reason = $"evidence '{id}' is captured in the future";
                return ActionOutcomes.Unverified;
            }

            // Staleness only bites where the policy actually demands evidence for this
            // capability; resolution and parseability are unconditional.
            if (required && captured < oldestAcceptable)
            {
                reason = $"evidence '{id}' is stale (captured {captured.ToWire()}, action started {started.ToWire()}, " +
                         $"min_interval_s {policy.MinIntervalSeconds})";
                return ActionOutcomes.Unverified;
            }
        }

        return record.Outcome;
    }
}
