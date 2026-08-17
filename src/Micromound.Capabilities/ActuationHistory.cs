namespace Micromound.Capabilities;

/// <summary>
/// When each capability last ran, and how often — the state duty-cycle and rate limits are
/// checked against.
///
/// This is deliberately per-capability rather than per-mission or per-charter. A relay's minimum
/// off-time is a property of the relay: it must hold across a charter replacement, a mission
/// change, and a reconnect, or a controller could reset a pump's cooldown by reissuing paperwork.
/// </summary>
public sealed class ActuationHistory
{
    private readonly Dictionary<string, DateTimeOffset> _lastEnd = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _starts = new(StringComparer.Ordinal);

    /// <summary>When this capability's last actuation finished, or null if it never has.</summary>
    public DateTimeOffset? LastEnd(string capability) =>
        _lastEnd.TryGetValue(capability, out var end) ? end : null;

    /// <summary>
    /// How many times this capability started in the trailing hour before <paramref name="now"/>.
    ///
    /// Read-only, deliberately. An earlier version pruned expired entries here, which made the
    /// kernel's <c>Authorize</c> destructive: a mound whose clock jumped forward — a bad RTC at
    /// boot, corrected by the first sync — would permanently delete every start older than an hour
    /// relative to the jumped clock, and once the clock was corrected back, the rate budget would
    /// be spuriously fresh inside the same real hour. Pruning belongs where state is already being
    /// changed, in <see cref="Record"/>.
    /// </summary>
    public int StartsInTrailingHour(string capability, DateTimeOffset now)
    {
        if (!_starts.TryGetValue(capability, out var starts)) return 0;

        var window = now.AddHours(-1);
        return starts.Count(at => at > window);
    }

    /// <summary>Record an actuation that actually ran. Refusals never land here — a refusal did nothing.</summary>
    public void Record(string capability, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        _lastEnd[capability] = endedAt;

        if (!_starts.TryGetValue(capability, out var starts))
        {
            starts = [];
            _starts[capability] = starts;
        }

        starts.Add(startedAt);

        // Prune here, where the caller is already mutating state and has supplied a timestamp we
        // are choosing to trust. Bounded by the widest window any rate limit can express.
        var window = startedAt.AddHours(-1);
        if (starts.Count > 1) starts.RemoveAll(at => at <= window);
    }

    /// <summary>Forget everything. For test setup and for a device that has been physically reset.</summary>
    public void Clear()
    {
        _lastEnd.Clear();
        _starts.Clear();
    }
}
