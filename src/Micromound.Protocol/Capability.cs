namespace Micromound.Protocol;

/// <summary>
/// The capability namespace — CAPABILITIES.md. Workers ask for semantic operations
/// (<c>act.water_valve</c>), never for hardware (<c>GPIO17 = HIGH</c>), and the namespace prefix
/// is what makes an id's kind readable without a registry lookup.
///
/// Three prefixes, closed: <c>sense.</c> observes, <c>act.</c> actuates, <c>routine.</c> invokes a
/// registered deterministic sequence. A fourth would need a protocol version bump, because
/// charters, evidence policies, and the ESP32 mirror all pattern-match on these.
/// </summary>
public static class CapabilityId
{
    public const string SensePrefix = "sense.";
    public const string ActPrefix = "act.";
    public const string RoutinePrefix = "routine.";

    public static readonly IReadOnlySet<string> Namespaces = new HashSet<string>(StringComparer.Ordinal)
    {
        "sense", "act", "routine"
    };

    public static bool IsSense(string id) => id.StartsWith(SensePrefix, StringComparison.Ordinal);
    public static bool IsAct(string id) => id.StartsWith(ActPrefix, StringComparison.Ordinal);
    public static bool IsRoutine(string id) => id.StartsWith(RoutinePrefix, StringComparison.Ordinal);

    /// <summary>The namespace segment, or "" when the id is not well formed.</summary>
    public static string NamespaceOf(string id)
    {
        var dot = id.IndexOf('.');
        if (dot <= 0) return "";
        var ns = id[..dot];
        return Namespaces.Contains(ns) ? ns : "";
    }

    /// <summary>
    /// A well-formed id is <c>&lt;namespace&gt;.&lt;segment&gt;[.&lt;segment&gt;…]</c> where every
    /// segment is lowercase <c>[a-z0-9_]</c>. Restrictive on purpose: these strings appear in
    /// charters, evidence patterns, and firmware tables, and a case-folding or Unicode question
    /// in any of those places is a question about whether two devices agree on authority.
    /// </summary>
    public static bool IsWellFormed(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        var segments = id.Split('.');
        if (segments.Length < 2) return false;
        if (!Namespaces.Contains(segments[0])) return false;

        foreach (var segment in segments)
        {
            if (segment.Length == 0) return false;
            foreach (var c in segment)
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                    return false;
        }

        return true;
    }
}

/// <summary>
/// Capability pattern matching for charter fields that take globs (<c>evidence.required_for</c>).
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

    /// <summary>True when any pattern in the set matches.</summary>
    public static bool MatchesAny(IEnumerable<string> patterns, string capability) =>
        patterns.Any(pattern => Matches(pattern, capability));
}
