using Micromound.Protocol;

namespace Micromound.Capabilities;

/// <summary>The mound's authority state, as reported to the controller and shown in a UI.</summary>
public static class MoundStates
{
    /// <summary>A stop order is in force. No actuation, at all, until it is explicitly cleared.</summary>
    public const string Stopped = "stopped";
    /// <summary>No charter. Sensing is permitted; nothing else is.</summary>
    public const string ObserveOnly = "observe_only";
    /// <summary>Lease ran out. Safe state entered, charter retained for reporting only.</summary>
    public const string Quiesced = "quiesced";
    /// <summary>A valid charter with a live lease.</summary>
    public const string Chartered = "chartered";
}

/// <summary>
/// Everything the kernel consults about authority, in one place — SAFETY.md Layer 2.
///
/// Pulling this out of the runtime is deliberate. Lease arithmetic, stop state, and the active
/// charter are the questions the answer to "may this actuation happen" turns on, and scattering
/// them across a coordinator, a worker, and a device object is how a mound ends up with two
/// disagreeing notions of whether its lease is alive.
///
/// Nothing here can extend a lease from the device side. <see cref="RenewLease"/> exists to be
/// called when a sync beat is acknowledged by the controller — that is the only renewal path,
/// and there is no method that grants authority the controller did not.
/// </summary>
public sealed class KernelAuthority(string moundId)
{
    private readonly Dictionary<string, CapabilityLimits> _deviceLimits = new(StringComparer.Ordinal);

    public string MoundId { get; } = moundId;

    public Charter? ActiveCharter { get; private set; }

    public DateTimeOffset LeaseExpiresAt { get; private set; } = DateTimeOffset.MinValue;

    public bool IsStopped { get; private set; }

    public bool IsQuiesced { get; private set; }

    /// <summary>The declared de-energized state, from the manifest and then the charter.</summary>
    public string SafeState { get; private set; } = "all_actuators_off";

    /// <summary>The middle limit tier: operator configuration, narrower than hardware.</summary>
    public IReadOnlyDictionary<string, CapabilityLimits> DeviceLimits => _deviceLimits;

    public string State =>
        IsStopped ? MoundStates.Stopped
        : ActiveCharter is null ? MoundStates.ObserveOnly
        : IsQuiesced ? MoundStates.Quiesced
        : MoundStates.Chartered;

    public bool LeaseAlive(DateTimeOffset now) =>
        ActiveCharter is not null && !IsQuiesced && !IsStopped && now < LeaseExpiresAt;

    /// <summary>Seconds of lease remaining, floored at zero — what a status display shows.</summary>
    public double LeaseRemainingSeconds(DateTimeOffset now) =>
        Math.Max(0, (LeaseExpiresAt - now).TotalSeconds);

    /// <summary>
    /// Apply a validated manifest. Device limits and the safe state come from here; capabilities
    /// and workers are the runtime's business, not the kernel's.
    /// </summary>
    public void ApplyManifest(MoundManifest manifest)
    {
        _deviceLimits.Clear();
        foreach (var (key, limits) in manifest.DeviceLimits) _deviceLimits[key] = limits;
        if (!string.IsNullOrWhiteSpace(manifest.SafeState)) SafeState = manifest.SafeState;
    }

    /// <summary>
    /// Offer a charter. Invalid charters leave state completely untouched — a mound that refuses
    /// a charter keeps operating under the one it already had, rather than dropping to
    /// observe-only because someone sent a malformed document.
    /// </summary>
    public ValidationResult AcceptCharter(Charter charter, DateTimeOffset now,
        IReadOnlySet<string>? deviceCapabilities = null, IReadOnlySet<string>? deviceRoutines = null)
    {
        var result = CharterValidator.Validate(charter, MoundId, now, deviceCapabilities, deviceRoutines);
        if (!result.IsValid) return result;

        // A stop order outranks a charter. Accepting one while stopped would let an operator
        // clear a stop by issuing paperwork, which is exactly what stop exists to prevent.
        if (IsStopped)
            return new ValidationResult(["mound is stopped; a stop must be explicitly cleared before a charter is accepted"]);

        ActiveCharter = charter;               // complete replacement, never a diff
        LeaseExpiresAt = now.AddSeconds(charter.LeaseTtlSeconds);
        IsQuiesced = false;                    // a fresh charter is the only way out of quiesce
        SafeState = charter.SafeState;
        return result;
    }

    /// <summary>
    /// The sync beat was acknowledged: the lease renews. Nothing else about authority changes,
    /// and a quiesced mound is not revived — PROTOCOL.md §5, resumption is never implicit.
    /// </summary>
    public void RenewLease(DateTimeOffset now)
    {
        if (ActiveCharter is not null && !IsStopped && !IsQuiesced)
            LeaseExpiresAt = now.AddSeconds(ActiveCharter.LeaseTtlSeconds);
    }

    /// <summary>
    /// Lease expiry check, driven by the device's own clock tick. Returns true on the transition
    /// so the caller can queue the `quiesced` report exactly once.
    /// </summary>
    public bool QuiesceIfExpired(DateTimeOffset now)
    {
        if (ActiveCharter is null || IsQuiesced || IsStopped || now < LeaseExpiresAt) return false;
        IsQuiesced = true;
        return true;
    }

    /// <summary>
    /// Stop wins over everything and needs no charter. The charter is dropped, not suspended, so
    /// that clearing the stop cannot silently restore the authority that was in force before it.
    /// </summary>
    public void Stop()
    {
        IsStopped = true;
        ActiveCharter = null;
        IsQuiesced = false;
        LeaseExpiresAt = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Clear a stop. This restores nothing: the mound returns to observe-only and waits for a
    /// fresh charter. Resume is always explicit, and it is never a resume of the old mission.
    /// </summary>
    public void ClearStop() => IsStopped = false;

    /// <summary>
    /// The ceiling actually in force. Every path that loses authority resolves to
    /// <see cref="ActionClass.Observe"/> rather than to an error — ambiguity resolves downward.
    /// </summary>
    public ActionClass EffectiveCeiling(DateTimeOffset now)
    {
        if (ActiveCharter is null) return ActionClass.Observe;
        if (!LeaseAlive(now)) return ActionClass.Observe;
        if (!ActionClasses.TryParse(ActiveCharter.ActionCeiling, out var ceiling)) return ActionClass.Observe;
        return ceiling == ActionClass.Hazardous ? ActionClass.Observe : ceiling;
    }

    /// <summary>The evidence policy in force. With no charter there is nothing to prove and nothing granted.</summary>
    public EvidencePolicy EffectiveEvidencePolicy() => ActiveCharter?.Evidence ?? new EvidencePolicy();

    /// <summary>The charter's limits for a capability or routine, or null when it sets none.</summary>
    public CapabilityLimits? CharterLimitsFor(string id) =>
        ActiveCharter is not null && ActiveCharter.Limits.TryGetValue(id, out var limits) ? limits : null;

    /// <summary>The operator's configured limits for a capability or routine, or null.</summary>
    public CapabilityLimits? DeviceLimitsFor(string id) =>
        _deviceLimits.TryGetValue(id, out var limits) ? limits : null;
}
