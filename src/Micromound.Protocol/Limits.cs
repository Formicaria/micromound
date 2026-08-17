namespace Micromound.Protocol;

/// <summary>
/// The three tiers of operating limit the kernel intersects, innermost first — SAFETY.md Layer 1.
/// Named so that argument order is never a guess at a call site.
/// </summary>
public enum LimitTier
{
    /// <summary>What the hardware or firmware build physically permits. Not remotely settable.</summary>
    Hardware = 0,
    /// <summary>Operator configuration from the mound manifest. Narrows hardware, permanently.</summary>
    Device = 1,
    /// <summary>The delegated grant in the active charter. Narrows both tiers beneath it.</summary>
    Charter = 2
}

/// <summary>
/// Enforces the intersection of hardware, device, and charter limits — SAFETY.md Layer 1.
/// The narrower bound always wins; an outer tier can only narrow, never widen.
///
/// The asymmetry in <see cref="Intersect(CapabilityLimits, CapabilityLimits)"/> is the whole
/// point: ceilings (<c>max_on_s</c>, <c>max</c>, <c>max_rate_per_h</c>) take the MINIMUM of the
/// tiers, floors (<c>min_off_s</c>, <c>min</c>) take the MAXIMUM. A charter asking for a shorter
/// off-time than the hardware demands does not get one.
/// </summary>
public static class LimitClamp
{
    /// <summary>
    /// Clamps a requested value into <c>[min, max]</c> where those bounds are set.
    /// Returns true when the request was outside the bound and had to be narrowed.
    /// </summary>
    public static bool ClampToRange(double requested, CapabilityLimits effective, out double allowed)
    {
        allowed = requested;
        if (effective.Min is { } min && allowed < min) allowed = min;
        if (effective.Max is { } max && allowed > max) allowed = max;
        return allowed != requested;
    }

    /// <summary>Clamps a requested on-time against <c>max_on_s</c>. True when it was narrowed.</summary>
    public static bool ClampOnSeconds(double requested, CapabilityLimits effective, out double allowed)
    {
        allowed = requested;
        if (effective.MaxOnSeconds is { } max && allowed > max) allowed = max;
        return allowed != requested;
    }

    /// <summary>Intersect two tiers. A null bound on either side means "that tier sets no bound".</summary>
    public static CapabilityLimits Intersect(CapabilityLimits inner, CapabilityLimits outer) => new()
    {
        MaxOnSeconds = MinOf(inner.MaxOnSeconds, outer.MaxOnSeconds),
        MinOffSeconds = MaxOf(inner.MinOffSeconds, outer.MinOffSeconds),
        Min = MaxOf(inner.Min, outer.Min),
        Max = MinOf(inner.Max, outer.Max),
        MaxRatePerHour = MinOf(inner.MaxRatePerHour, outer.MaxRatePerHour)
    };

    /// <summary>Intersect three tiers. Associative, so the grouping here carries no meaning.</summary>
    public static CapabilityLimits Intersect(CapabilityLimits hardware, CapabilityLimits device,
        CapabilityLimits charter) =>
        Intersect(Intersect(hardware, device), charter);

    /// <summary>
    /// The bound actually enforced for one capability: hardware ∩ device ∩ charter, with any
    /// absent tier treated as "sets no bound of its own".
    /// </summary>
    public static CapabilityLimits Effective(CapabilityLimits? hardware, CapabilityLimits? device,
        CapabilityLimits? charter) =>
        Intersect(hardware ?? new CapabilityLimits(), device ?? new CapabilityLimits(),
            charter ?? new CapabilityLimits());

    /// <summary>
    /// True when <paramref name="outer"/> tries to loosen any bound set by <paramref name="inner"/>.
    /// The kernel never needs this — it intersects, so a widening attempt is simply ignored — but
    /// validation reports the attempt, because SAFETY.md forbids silent anything.
    /// </summary>
    public static bool AttemptsToWiden(CapabilityLimits inner, CapabilityLimits outer) =>
        Widens(inner.MaxOnSeconds, outer.MaxOnSeconds, higherIsWider: true) ||
        Widens(inner.Max, outer.Max, higherIsWider: true) ||
        Widens(inner.MaxRatePerHour, outer.MaxRatePerHour, higherIsWider: true) ||
        Widens(inner.MinOffSeconds, outer.MinOffSeconds, higherIsWider: false) ||
        Widens(inner.Min, outer.Min, higherIsWider: false);

    private static bool Widens(double? inner, double? outer, bool higherIsWider)
    {
        if (inner is null || outer is null) return false;
        return higherIsWider ? outer.Value > inner.Value : outer.Value < inner.Value;
    }

    private static double? MinOf(double? a, double? b) =>
        a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private static double? MaxOf(double? a, double? b) =>
        a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
