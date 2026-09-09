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
///
/// <para><b>Time here is measured twice</b> (`v0.9.38`, roadmap P0.5). Every recorded instant is a
/// wall-clock time, because that is what persists and what an operator reads; but a wall clock can
/// be STEPPED, and a step forward is indistinguishable from time passing. That is the whole problem:
/// a mound whose RTC is an hour slow at boot, corrected by the first NTP sync, would find every
/// cooldown elapsed and every rate budget fresh at the exact moment it has least reason to trust
/// itself. So when a <see cref="Time"/> provider is supplied, each entry also carries the monotonic
/// timestamp at which it was recorded, and the age of an entry is the SMALLER of what the two clocks
/// claim. For "has enough time passed?" the smaller answer is the safe one — the mirror image of the
/// rule `v0.9.31` uses for releasing a hold, where the question is "is it time to de-energize?" and
/// the LARGER elapsed wins.</para>
/// </summary>
public sealed class ActuationHistory
{
    private readonly Dictionary<string, Instant> _lastEnd = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Instant>> _starts = new(StringComparer.Ordinal);

    /// <summary>
    /// The monotonic source used to cross-check the wall clock, or null for wall-clock only.
    ///
    /// <para>Null is not a lapse: it is the honest state for a history just restored from disk (the
    /// stamps of a previous process mean nothing in this one), for the in-memory kernels the golden
    /// fixtures drive, and for any composition with no real clock behind it. What null costs is
    /// stated rather than hidden — see the class note. A real host sets it.</para>
    /// </summary>
    public TimeProvider? Time { get; set; }

    /// <summary>When this capability's last actuation finished, or null if it never has.</summary>
    public DateTimeOffset? LastEnd(string capability) =>
        _lastEnd.TryGetValue(capability, out var end) ? end.Wall : null;

    /// <summary>
    /// Whether <c>min_off_s</c> has really elapsed since this capability last ran.
    ///
    /// <para>Asked as a question rather than answered by the caller subtracting from
    /// <see cref="LastEnd"/>, because the answer is not a subtraction: with a monotonic source in
    /// hand it is the smaller of what the two clocks say has passed, so a clock stepped forward
    /// cannot hand back a cooldown. <see cref="LastEnd"/> stays what the refusal DETAIL names — an
    /// operator reading a record needs the instant the hardware actually stopped, not the number the
    /// guard used to decide.</para>
    /// </summary>
    public bool MinOffElapsed(string capability, double minOffSeconds, DateTimeOffset now)
    {
        if (!_lastEnd.TryGetValue(capability, out var end)) return true;
        return Elapsed(end, now) >= TimeSpan.FromSeconds(minOffSeconds);
    }

    /// <summary>
    /// How many times this capability started in the trailing hour before <paramref name="now"/>.
    ///
    /// Read-only, deliberately. An earlier version pruned expired entries here, which made the
    /// kernel's <c>Authorize</c> destructive: a mound whose clock jumped forward — a bad RTC at
    /// boot, corrected by the first sync — would permanently delete every start older than an hour
    /// relative to the jumped clock, and once the clock was corrected back, the rate budget would
    /// be spuriously fresh inside the same real hour. Pruning belongs where state is already being
    /// changed, in <see cref="Record"/>.
    ///
    /// <para>The window is now aged on the smaller of wall and monotonic elapsed, so the forward
    /// step that pruning would have made permanent cannot even make it temporary.</para>
    /// </summary>
    public int StartsInTrailingHour(string capability, DateTimeOffset now)
    {
        if (!_starts.TryGetValue(capability, out var starts)) return 0;

        return starts.Count(at => Elapsed(at, now) < TimeSpan.FromHours(1));
    }

    /// <summary>Record an actuation that actually ran. Refusals never land here — a refusal did nothing.</summary>
    public void Record(string capability, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        var stamp = Time?.GetTimestamp();
        _lastEnd[capability] = new Instant(endedAt, stamp);

        if (!_starts.TryGetValue(capability, out var starts))
        {
            starts = [];
            _starts[capability] = starts;
        }

        starts.Add(new Instant(startedAt, stamp));

        // Prune here, where the caller is already mutating state and has supplied a timestamp we
        // are choosing to trust. Bounded by the widest window any rate limit can express — and by
        // the same two-clock rule the reads use, so a stepped clock cannot make a prune permanent.
        if (starts.Count > 1) starts.RemoveAll(at => Elapsed(at, startedAt) >= TimeSpan.FromHours(1));
    }

    /// <summary>
    /// How long ago an entry happened, believing whichever clock claims LESS.
    ///
    /// <para>With no monotonic source, or for an entry restored from a previous process (whose
    /// stamps mean nothing here), this is the wall-clock difference and nothing more. That is the
    /// residual P0.5 names: across a restart the real gap is unknowable from inside the mound, and
    /// no mechanism in this class can close it — only the controller, which knows what time it is,
    /// could.</para>
    /// </summary>
    private TimeSpan Elapsed(Instant at, DateTimeOffset now)
    {
        var wall = now - at.Wall;
        if (Time is not { } time || at.Stamp is not { } stamp) return wall;

        var monotonic = time.GetElapsedTime(stamp);
        return wall < monotonic ? wall : monotonic;
    }

    /// <summary>
    /// The snapshot this history persists as, and restores from — `v0.9.33`, roadmap P0.5. Until then
    /// this class was two in-memory dictionaries and nothing wrote them anywhere, so `min_off_s` and
    /// `max_rate_per_h` — limits a charter grants and the kernel enforces — began empty on every
    /// boot. A 300 s cooldown was enforced before a restart and gone three seconds after one, which
    /// made a reboot loop a way to actuate as often as you liked.
    ///
    /// <para>Monotonic stamps are deliberately NOT persisted: a reading from a counter that reset
    /// when the process did would be a number pretending to be evidence.</para>
    /// </summary>
    public ActuationHistorySnapshot Snapshot(DateTimeOffset now) => new()
    {
        SavedAt = now.ToWire(),
        Capabilities = _starts.Keys.Union(_lastEnd.Keys, StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .Select(capability => new ActuationHistoryEntry
            {
                Capability = capability,
                LastEnd = _lastEnd.TryGetValue(capability, out var end) ? end.Wall.ToWire() : "",
                Starts = (_starts.TryGetValue(capability, out var starts) ? starts : [])
                    .Select(at => at.Wall.ToWire()).ToList()
            })
            .ToList()
    };

    /// <summary>
    /// Rehydrate from a snapshot, replacing whatever is here. Unparseable timestamps are dropped
    /// rather than defaulted — a budget entry nobody can read is not a budget entry, and inventing
    /// one would be worse than losing it. Restored entries carry no monotonic stamp, so they age on
    /// the wall clock alone until a fresh actuation replaces them.
    /// </summary>
    public void Restore(ActuationHistorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Clear();

        foreach (var entry in snapshot.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(entry.Capability)) continue;

            if (ProtocolTime.TryParse(entry.LastEnd, out var end))
                _lastEnd[entry.Capability] = new Instant(end, null);

            var starts = entry.Starts
                .Select(at => ProtocolTime.TryParse(at, out var parsed) ? parsed : (DateTimeOffset?)null)
                .Where(at => at is not null)
                .Select(at => new Instant(at!.Value, null))
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

    /// <summary>A recorded moment: what the wall clock said, and what the monotonic counter said if there was one.</summary>
    private readonly record struct Instant(DateTimeOffset Wall, long? Stamp);
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
/// <c>saved_at</c> is provenance: when the writing mound believed it wrote this. It is not used to
/// correct for a stepped clock, and cannot be: the gap across a restart is unknowable from inside
/// the mound — see the note on rate accounting in ROADMAP P0.5.
/// </summary>
public sealed class ActuationHistorySnapshot
{
    [JsonPropertyName("saved_at")] public string SavedAt { get; set; } = "";
    [JsonPropertyName("capabilities")] public List<ActuationHistoryEntry> Capabilities { get; set; } = [];
}
