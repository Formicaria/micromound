namespace Micromound.Protocol;

/// <summary>
/// Capability pattern matching for charter fields that take globs (`evidence.required_for`).
/// Deliberately tiny: exact match, a trailing ".*" prefix match, or "*". Nothing else, because
/// the ESP32 mirror has to implement the same rule in C.
/// </summary>
public static class CapabilityPattern
{
    public static bool Matches(string pattern, string capability)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (pattern == "*") return true;

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^1]; // "act.*" -> "act."
            return capability.StartsWith(prefix, StringComparison.Ordinal);
        }

        return string.Equals(pattern, capability, StringComparison.Ordinal);
    }
}

/// <summary>
/// "Commands are not evidence" (MICROMOUND.md design rule 3), enforced as a pure function.
/// An action that claims physical work happened keeps its optimistic outcome only if the
/// evidence it references actually resolves, parses, and is fresh. Everything else is
/// <c>unverified</c> — never silently assumed done.
/// </summary>
public static class EvidenceGate
{
    public static bool RequiresEvidence(EvidencePolicy policy, string capability) =>
        policy.RequiredFor.Any(pattern => CapabilityPattern.Matches(pattern, capability));

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

        if (record.Outcome != ActionOutcomes.Succeeded && record.Outcome != ActionOutcomes.Clamped)
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

            // Staleness only bites where the charter actually demands evidence for this
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
