namespace Micromound.Protocol;

/// <summary>
/// Deterministic, fail-closed validation. Every rejection carries its full reason list — loud,
/// never silent (ANTHILL ContractGate discipline). No I/O, no clock reads: callers pass `now`
/// so validation is pure and trivially testable.
/// </summary>
public static class CharterValidator
{
    public static ValidationResult Validate(Charter charter, string expectedMoundId, DateTimeOffset now,
        IReadOnlySet<string>? deviceCapabilities = null)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(charter.CharterId)) errors.Add("charter_id missing");
        if (string.IsNullOrWhiteSpace(charter.MoundId)) errors.Add("mound_id missing");
        else if (!string.Equals(charter.MoundId, expectedMoundId, StringComparison.Ordinal))
            errors.Add($"mound_id mismatch: charter is for '{charter.MoundId}', this mound is '{expectedMoundId}'");

        if (!ActionClasses.TryParse(charter.ActionCeiling, out var ceiling))
            errors.Add($"action_ceiling unknown: '{charter.ActionCeiling}'");
        else if (ceiling == ActionClass.Hazardous)
            errors.Add("action_ceiling 'hazardous' is never a legal charter ceiling (per-action authorization only)");

        if (!DateTimeOffset.TryParse(charter.ExpiresAt, out var expires))
            errors.Add($"expires_at unparseable: '{charter.ExpiresAt}'");
        else if (expires <= now)
            errors.Add("charter already expired");

        if (!DateTimeOffset.TryParse(charter.IssuedAt, out var issued))
            errors.Add($"issued_at unparseable: '{charter.IssuedAt}'");
        else if (DateTimeOffset.TryParse(charter.ExpiresAt, out var exp2) && exp2 <= issued)
            errors.Add("expires_at precedes issued_at");

        if (charter.LeaseTtlSeconds <= 0) errors.Add("lease_ttl_s must be positive");
        if (charter.SyncIntervalSeconds <= 0) errors.Add("sync_interval_s must be positive");
        if (string.IsNullOrWhiteSpace(charter.SafeState)) errors.Add("safe_state missing");

        if (deviceCapabilities is not null)
            foreach (var cap in charter.Capabilities)
                if (!deviceCapabilities.Contains(cap))
                    errors.Add($"capability '{cap}' is not physically present on this device");

        return new ValidationResult(errors);
    }
}

public static class EnvelopeValidator
{
    public static ValidationResult Validate(Envelope envelope, bool reducedProfile = false)
    {
        var errors = new List<string>();

        if (envelope.Version != ProtocolVersion.Current)
            errors.Add($"unsupported protocol version {envelope.Version}");
        if (string.IsNullOrWhiteSpace(envelope.Id)) errors.Add("id missing");
        if (string.IsNullOrWhiteSpace(envelope.MoundId)) errors.Add("mound_id missing");
        if (envelope.Seq < 0) errors.Add("seq negative");

        var kinds = reducedProfile ? EnvelopeKinds.ReducedProfile : EnvelopeKinds.All;
        if (!kinds.Contains(envelope.Kind))
            errors.Add($"refused_unknown_kind: '{envelope.Kind}'");

        if (!DateTimeOffset.TryParse(envelope.SentAt, out _))
            errors.Add($"sent_at unparseable: '{envelope.SentAt}'");

        return new ValidationResult(errors);
    }

    /// <summary>
    /// Verifies the uplink hash chain — PROTOCOL.md §6. Gaps and reordering after offline
    /// periods must be detectable, so each envelope's prev_digest must equal the digest of the
    /// envelope before it. The first envelope's prev_digest is checked against `anchorDigest`
    /// (the last acknowledged digest, or "" for a fresh chain).
    /// </summary>
    public static ValidationResult ValidateChain(IReadOnlyList<Envelope> ordered, string anchorDigest)
    {
        var errors = new List<string>();
        var expected = anchorDigest;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!string.Equals(ordered[i].PrevDigest, expected, StringComparison.Ordinal))
                errors.Add($"chain break at index {i} (seq {ordered[i].Seq}): expected prev_digest '{expected}', got '{ordered[i].PrevDigest}'");
            if (i > 0 && ordered[i].Seq != ordered[i - 1].Seq + 1)
                errors.Add($"seq gap at index {i}: {ordered[i - 1].Seq} -> {ordered[i].Seq}");
            expected = ordered[i].Digest();
        }
        return new ValidationResult(errors);
    }
}

/// <summary>
/// Enforces the intersection of firmware limits and charter limits — SAFETY.md Layer 1.
/// The narrower bound always wins; a charter can only narrow, never widen.
/// </summary>
public static class LimitClamp
{
    public static CapabilityLimits Intersect(CapabilityLimits firmware, CapabilityLimits charter) => new()
    {
        MaxOnSeconds = MinOf(firmware.MaxOnSeconds, charter.MaxOnSeconds),
        MinOffSeconds = MaxOf(firmware.MinOffSeconds, charter.MinOffSeconds),
        Min = MaxOf(firmware.Min, charter.Min),
        Max = MinOf(firmware.Max, charter.Max),
        MaxRatePerHour = MinOf(firmware.MaxRatePerHour, charter.MaxRatePerHour)
    };

    private static double? MinOf(double? a, double? b) =>
        a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private static double? MaxOf(double? a, double? b) =>
        a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}

public sealed class ValidationResult(List<string> errors)
{
    public IReadOnlyList<string> Errors { get; } = errors;
    public bool IsValid => Errors.Count == 0;
}
