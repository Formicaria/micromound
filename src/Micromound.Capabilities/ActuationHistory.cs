using System.Text.Json.Serialization;
using Micromound.Protocol;

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

    /// <summary>
    /// The snapshot this history persists as, and restores from — `v0.9.33`, roadmap P0.5. Until then
    /// this class was two in-memory dictionaries and nothing wrote them anywhere, so `min_off_s` and
    /// `max_rate_per_h` — limits a charter grants and the kernel enforces — began empty on every
    /// boot. A 300 s cooldown was enforced before a restart and gone three seconds after one, which
    /// made a reboot loop a way to actuate as often as you liked.
    /// </summary>
    public ActuationHistorySnapshot Snapshot(DateTimeOffset now) => new()
    {
        SavedAt = now.ToWire(),
        Capabilities = _starts.Keys.Union(_lastEnd.Keys, StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .Select(capability => new ActuationHistoryEntry
            {
                Capability = capability,
                LastEnd = _lastEnd.TryGetValue(capability, out var end) ? end.ToWire() : "",
                Starts = (_starts.TryGetValue(capability, out var starts) ? starts : [])
                    .Select(at => at.ToWire()).ToList()
            })
            .ToList()
    };

    /// <summary>
    /// Rehydrate from a snapshot, replacing whatever is here. Unparseable timestamps are dropped
    /// rather than defaulted — a budget entry nobody can read is not a budget entry, and inventing
    /// one would be worse than losing it. `saved_at` becomes the monotone reference, so a mound that
    /// comes up with a clock BEHIND the one that wrote the snapshot still ages its window from the
    /// later, known-real instant instead of handing back a spent budget.
    /// </summary>
    public void Restore(ActuationHistorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Clear();

        foreach (var entry in snapshot.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(entry.Capability)) continue;

            if (ProtocolTime.TryParse(entry.LastEnd, out var end)) _lastEnd[entry.Capability] = end;

            var starts = entry.Starts
                .Select(at => ProtocolTime.TryParse(at, out var parsed) ? parsed : (DateTimeOffset?)null)
                .Where(at => at is not null)
                .Select(at => at!.Value)
                .ToList();
            if (starts.Count > 0) _starts[entry.Capability] = starts;
        }

    }

    /// <summary>Forget everything. For test setup and for a device that has been physically reset.</summary>
    public void Clear()
    {
        _lastEnd.Clear();
        _starts.Clear();
    }
}

/// <summary>One capability's operating budget, as it persists. Timestamps are protocol strings, so
/// the file is legible to an operator and parses under the same rules as everything else.</summary>
public sealed class ActuationHistoryEntry
{
    [JsonPropertyName("capability")] public string Capability { get; set; } = "";
    /// <summary>When the last actuation ENDED — the reference `min_off_s` counts from. Empty if never.</summary>
    [JsonPropertyName("last_end")] public string LastEnd { get; set; } = "";
    /// <summary>Start instants inside the widest rate window, for `max_rate_per_h`.</summary>
    [JsonPropertyName("starts")] public List<string> Starts { get; set; } = [];
}

/// <summary>
/// The whole operating history as it persists — see <see cref="ActuationHistory.Snapshot"/>.
/// <c>saved_at</c> is provenance: when the writing mound believed it wrote this. It is not currently
/// used to correct for a stepped clock — see the note on rate accounting in ROADMAP P0.5.
/// </summary>
public sealed class ActuationHistorySnapshot
{
    [JsonPropertyName("saved_at")] public string SavedAt { get; set; } = "";
    [JsonPropertyName("capabilities")] public List<ActuationHistoryEntry> Capabilities { get; set; } = [];
}
