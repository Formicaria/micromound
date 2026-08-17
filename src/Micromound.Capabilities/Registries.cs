using Micromound.Protocol;

namespace Micromound.Capabilities;

/// <summary>
/// What this mound can physically do — CAPABILITIES.md. Populated at startup from the hardware
/// manifest and the drivers that back it, then read-only for the rest of the process.
///
/// Registration validates rather than trusting, and a rejected descriptor is simply not
/// registered: a mound that came up with a malformed capability refuses requests for it as
/// <see cref="RefusalReason.UnknownCapability"/> instead of running it with unclear bounds.
/// </summary>
public sealed class CapabilityRegistry
{
    private readonly Dictionary<string, CapabilityDescriptor> _capabilities = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Ids => _capabilities.Keys;

    public IEnumerable<CapabilityDescriptor> All => _capabilities.Values;

    /// <summary>
    /// Register one capability. Returns the reasons it was rejected; an empty result means it is
    /// now registered. Re-registering an id replaces it — drivers re-register on health changes.
    /// </summary>
    public ValidationResult Register(CapabilityDescriptor descriptor)
    {
        var errors = Validate(descriptor);
        if (errors.Count == 0) _capabilities[descriptor.Id] = descriptor;
        return new ValidationResult(errors);
    }

    public bool TryGet(string id, out CapabilityDescriptor descriptor) =>
        _capabilities.TryGetValue(id, out descriptor!);

    public bool Contains(string id) => _capabilities.ContainsKey(id);

    /// <summary>The set a charter validator checks its granted capabilities against.</summary>
    public IReadOnlySet<string> DeclaredCapabilities() =>
        new HashSet<string>(_capabilities.Keys, StringComparer.Ordinal);

    /// <summary>Capabilities at or below a class — what "observe only" resolves to with no charter.</summary>
    public IReadOnlySet<string> AtOrBelow(ActionClass ceiling) =>
        new HashSet<string>(
            _capabilities.Values.Where(d => d.Class <= ceiling).Select(d => d.Id),
            StringComparer.Ordinal);

    internal static List<string> Validate(CapabilityDescriptor descriptor)
    {
        var errors = new List<string>();

        if (!CapabilityId.IsWellFormed(descriptor.Id))
            errors.Add($"capability id '{descriptor.Id}' is not well formed");

        // A sensor classified as actuation would pass an actuation ceiling it has no business
        // passing, and a sensor classified below Observe does not exist. Pin it.
        if (CapabilityId.IsSense(descriptor.Id) && descriptor.Class != ActionClass.Observe)
            errors.Add($"'{descriptor.Id}' is in the sense namespace and must be class 'observe'");

        // And the converse, which matters more, because Class defaults to Observe: an actuator
        // that forgot to declare its class would otherwise be registered as an observation —
        // executable with no charter at all, and exempt from duty-cycle and rate limits, which
        // apply only above Observe. One missing initializer line should not produce that.
        if (CapabilityId.IsAct(descriptor.Id) && descriptor.Class == ActionClass.Observe)
            errors.Add($"'{descriptor.Id}' is in the act namespace and must declare a class above 'observe'");

        if (descriptor.Class == ActionClass.Hazardous)
            errors.Add($"'{descriptor.Id}' cannot be registered as 'hazardous': hazardous work has no authorization pipeline yet");

        if (CapabilityId.IsRoutine(descriptor.Id))
            errors.Add($"'{descriptor.Id}' is a routine id and belongs in the routine registry");

        foreach (var required in descriptor.RequiredParameters)
            if (!descriptor.Parameters.Contains(required))
                errors.Add($"'{descriptor.Id}': required parameter '{required}' is not an accepted parameter");

        foreach (var ranged in descriptor.ParameterRanges.Keys)
            if (!descriptor.Parameters.Contains(ranged))
                errors.Add($"'{descriptor.Id}': parameter range declared for unknown parameter '{ranged}'");

        if (descriptor.DurationParameter is { } duration && !descriptor.Parameters.Contains(duration))
            errors.Add($"'{descriptor.Id}': duration parameter '{duration}' is not an accepted parameter");

        if (descriptor.MagnitudeParameter is { } magnitude && !descriptor.Parameters.Contains(magnitude))
            errors.Add($"'{descriptor.Id}': magnitude parameter '{magnitude}' is not an accepted parameter");

        return errors;
    }
}

/// <summary>
/// The routines this build offers — ARCHITECTURE.md "Routines". On a Pi these are registered by
/// the runtime; on a controller they are compiled into the firmware image and the registry is a
/// static table. Either way a charter selects from this set and never adds to it.
/// </summary>
public sealed class RoutineRegistry(CapabilityRegistry capabilities)
{
    private readonly CapabilityRegistry _capabilities = capabilities;
    private readonly Dictionary<string, RoutineDescriptor> _routines = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Ids => _routines.Keys;

    public IEnumerable<RoutineDescriptor> All => _routines.Values;

    public ValidationResult Register(RoutineDescriptor descriptor)
    {
        var errors = new List<string>();

        if (!CapabilityId.IsRoutine(descriptor.Id))
            errors.Add($"routine id '{descriptor.Id}' must be in the 'routine.' namespace");
        else if (!CapabilityId.IsWellFormed(descriptor.Id))
            errors.Add($"routine id '{descriptor.Id}' is not well formed");

        if (descriptor.Class == ActionClass.Hazardous)
            errors.Add($"'{descriptor.Id}' cannot be registered as 'hazardous': hazardous work has no authorization pipeline yet");

        if (descriptor.RequiredCapabilities.Count == 0)
            errors.Add($"'{descriptor.Id}' drives no capabilities");

        foreach (var capability in descriptor.RequiredCapabilities)
        {
            if (!_capabilities.TryGet(capability, out var backing))
            {
                errors.Add($"'{descriptor.Id}' requires capability '{capability}', which is not registered");
                continue;
            }

            // A routine cannot be a way to reach a capability at a lower class than it really is.
            if (backing.Class > descriptor.Class)
                errors.Add($"'{descriptor.Id}' is class '{ActionClasses.ToWire(descriptor.Class)}' but drives " +
                           $"'{capability}', which is class '{ActionClasses.ToWire(backing.Class)}'");
        }

        foreach (var required in descriptor.RequiredParameters)
            if (!descriptor.Parameters.Contains(required))
                errors.Add($"'{descriptor.Id}': required parameter '{required}' is not an accepted parameter");

        foreach (var ranged in descriptor.ParameterRanges.Keys)
            if (!descriptor.Parameters.Contains(ranged))
                errors.Add($"'{descriptor.Id}': parameter range declared for unknown parameter '{ranged}'");

        if (descriptor.DurationParameter is { } duration && !descriptor.Parameters.Contains(duration))
            errors.Add($"'{descriptor.Id}': duration parameter '{duration}' is not an accepted parameter");

        if (descriptor.MagnitudeParameter is { } magnitude && !descriptor.Parameters.Contains(magnitude))
            errors.Add($"'{descriptor.Id}': magnitude parameter '{magnitude}' is not an accepted parameter");

        if (errors.Count == 0) _routines[descriptor.Id] = descriptor;
        return new ValidationResult(errors);
    }

    public bool TryGet(string id, out RoutineDescriptor descriptor) =>
        _routines.TryGetValue(id, out descriptor!);

    public bool Contains(string id) => _routines.ContainsKey(id);

    public IReadOnlySet<string> DeclaredRoutines() =>
        new HashSet<string>(_routines.Keys, StringComparer.Ordinal);
}
