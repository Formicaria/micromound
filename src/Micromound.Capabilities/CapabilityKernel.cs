using System.Globalization;
using Micromound.Protocol;

namespace Micromound.Capabilities;

/// <summary>
/// The physical authority boundary — SAFETY.md Layer 1, ARCHITECTURE.md Layer 3.
///
/// Every actuation on every tier passes through here. Not "should": the drivers are reachable
/// only through <see cref="ICapabilityExecutor"/>, executors are held only by this class, and
/// nothing hands one out. A worker, a workflow, a mission from the controller, and a local model
/// all arrive at the same function with the same arguments and get the same answer.
///
/// The check order below is itself a safety statement, and it is worth reading as one:
///
///   1. stop            — precedes everything, needs no charter, cannot be overridden
///   2. known           — an id this build actually registers
///   3. available       — a driver that reports itself healthy
///   4. hazardous       — refused unconditionally until the M5 pipeline exists
///   5. authority       — action class against the ceiling the charter and lease leave in force
///   6. granted         — this specific capability or routine, in this specific charter
///   7. worker ceiling  — the requesting ant's own declared limit, intersected with the above
///   8. parameters      — accepted names, required names present
///   9. limits          — hardware ∩ device ∩ charter, narrowest wins
///  10. duty cycle      — minimum off-time since this capability last ran
///  11. rate            — actuations in the trailing hour
///  12. clamp           — narrow the request rather than refusing it, and say so
///  13. executor        — something is actually wired up to do it
///
/// Authorization is a pure function of (registries, authority, history, request, now), so the
/// rules are testable with no hardware, no simulator, and no wall clock.
/// </summary>
public sealed class CapabilityKernel(
    CapabilityRegistry capabilities,
    RoutineRegistry routines,
    KernelAuthority authority,
    ActuationHistory? history = null)
{
    private readonly Dictionary<string, ICapabilityExecutor> _executors = new(StringComparer.Ordinal);

    public CapabilityRegistry Capabilities { get; } = capabilities;

    public RoutineRegistry Routines { get; } = routines;

    public KernelAuthority Authority { get; } = authority;

    public ActuationHistory History { get; } = history ?? new ActuationHistory();

    /// <summary>
    /// Bind a driver to a capability or routine. Returns the reasons it was rejected; an empty
    /// result means it is bound. An executor for an id nothing registers is refused rather than
    /// stored, because a silently dead executor looks exactly like a working one until it is
    /// needed.
    /// </summary>
    public ValidationResult RegisterExecutor(ICapabilityExecutor executor)
    {
        var errors = new List<string>();
        var id = executor.CapabilityId;

        if (!Capabilities.Contains(id) && !Routines.Contains(id))
            errors.Add($"executor for '{id}' matches no registered capability or routine");

        if (errors.Count == 0) _executors[id] = executor;
        return new ValidationResult(errors);
    }

    public bool HasExecutor(string id) => _executors.ContainsKey(id);

    /// <summary>
    /// Advisory review of a charter against what this device physically permits. Returns a note
    /// for every limit the charter tries to loosen.
    ///
    /// These are not refusals — a widening attempt is already inert, because the kernel
    /// intersects rather than replaces. But SAFETY.md forbids silent anything, and a charter
    /// author who believes they granted a 600-second run on a 30-second relay has a
    /// misunderstanding that will otherwise surface as an unexplained clamp in the field.
    /// </summary>
    public ValidationResult ReviewCharter(Charter charter)
    {
        var notes = new List<string>();

        foreach (var (id, charterLimits) in charter.Limits)
        {
            CapabilityLimits? hardware = null;
            if (Capabilities.TryGet(id, out var capability)) hardware = capability.HardwareLimits;
            else if (Routines.TryGet(id, out var routine)) hardware = routine.HardwareLimits;

            if (hardware is not null && LimitClamp.AttemptsToWiden(hardware, charterLimits))
                notes.Add($"charter limits for '{id}' try to widen the hardware bound; the hardware bound stands");

            var device = Authority.DeviceLimitsFor(id);
            if (device is not null && LimitClamp.AttemptsToWiden(device, charterLimits))
                notes.Add($"charter limits for '{id}' try to widen the configured device bound; the device bound stands");
        }

        return new ValidationResult(notes);
    }

    // ---------------------------------------------------------------------------------------
    // Authorization
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Decide whether this request may proceed, and on what terms. No side effects: nothing is
    /// recorded, no hardware moves, and calling it twice answers twice the same.
    /// </summary>
    public KernelDecision Authorize(CapabilityRequest request, DateTimeOffset now)
    {
        var requested = new Dictionary<string, double>(request.Parameters, StringComparer.Ordinal);

        // 1. Stop precedes all other downlink and needs no valid charter (SAFETY.md Layer 3).
        if (Authority.IsStopped)
            return KernelDecision.Refuse(RefusalReason.Stopped,
                "a stop order is in force; stop precedes all work", requested);

        // 2 & 3. Resolve what was asked for, and refuse specifically if it does not resolve.
        var target = Resolve(request.Capability, requested, out var resolutionFailure);
        if (target is null) return resolutionFailure!;

        // Availability has two independent sources: the manifest (an operator disabled it) and
        // the driver's own health (it faulted since boot). Either one refuses.
        if (_executors.TryGetValue(target.Id, out var bound) && !bound.IsAvailable)
            return KernelDecision.Refuse(RefusalReason.CapabilityUnavailable,
                $"'{target.Id}': driver reports itself unavailable", requested);

        // 4. Hazardous work has no per-action authorization pipeline yet, so it cannot happen.
        if (target.Class == ActionClass.Hazardous)
            return KernelDecision.Refuse(RefusalReason.HazardousProhibited,
                $"'{target.Id}' is hazardous class; hazardous work requires per-action authorization that does not exist yet",
                requested);

        // 5. Authority: what class may run at all right now.
        var ceiling = Authority.EffectiveCeiling(now);
        if (target.Class > ceiling)
        {
            if (Authority.ActiveCharter is null)
                return KernelDecision.Refuse(RefusalReason.NoCharter,
                    $"no charter: authority is 'observe' only, '{target.Id}' is class '{ActionClasses.ToWire(target.Class)}'",
                    requested);

            if (!Authority.LeaseAlive(now))
                return KernelDecision.Refuse(RefusalReason.LeaseExpired,
                    $"lease expired at {Authority.LeaseExpiresAt.ToWire()}; awaiting a fresh charter", requested);

            return KernelDecision.Refuse(RefusalReason.ActionClassExceeded,
                $"'{target.Id}' is class '{ActionClasses.ToWire(target.Class)}', charter ceiling is " +
                $"'{ActionClasses.ToWire(ceiling)}'", requested);
        }

        // 6. Granted. With no charter the grant is exactly "every registered observe capability";
        //    with one, it is exactly what that charter lists, because a charter is a complete
        //    replacement and never a diff.
        if (Authority.ActiveCharter is { } charter)
        {
            var granted = target.IsRoutine
                ? charter.Routines.Contains(target.Id)
                : charter.Capabilities.Contains(target.Id);

            if (!granted)
                return KernelDecision.Refuse(
                    target.IsRoutine ? RefusalReason.RoutineNotEnabled : RefusalReason.NotGranted,
                    $"charter '{charter.CharterId}' does not " +
                    (target.IsRoutine ? "enable routine " : "grant capability ") + $"'{target.Id}'",
                    requested);
        }
        else if (target.Class > ActionClass.Observe)
        {
            // Unreachable via the ceiling check above; kept because "no charter means observe"
            // is a rule worth stating twice rather than inferring from arithmetic.
            return KernelDecision.Refuse(RefusalReason.NoCharter,
                $"no charter: '{target.Id}' is not observe class", requested);
        }

        // 7. The requesting worker's own ceiling. A Scout Ant does not actuate, whatever the
        //    charter would otherwise permit.
        if (request.WorkerCeiling is { } workerCeiling && target.Class > workerCeiling)
            return KernelDecision.Refuse(RefusalReason.ActionClassExceeded,
                $"worker '{request.Worker}' has ceiling '{ActionClasses.ToWire(workerCeiling)}', " +
                $"'{target.Id}' is class '{ActionClasses.ToWire(target.Class)}'", requested);

        // 8. Parameters. An unknown parameter is refused rather than dropped: a caller that
        //    misspelled `on_s` asked for something, and quietly running without it is worse.
        foreach (var name in requested.Keys)
            if (!target.Parameters.Contains(name))
                return KernelDecision.Refuse(RefusalReason.UnknownParameter,
                    $"'{target.Id}' does not accept parameter '{name}'", requested);

        foreach (var name in target.RequiredParameters)
            if (!requested.ContainsKey(name))
                return KernelDecision.Refuse(RefusalReason.MissingParameter,
                    $"'{target.Id}' requires parameter '{name}'", requested);

        // 9. The bound actually in force: hardware ∩ device ∩ charter.
        var effectiveLimits = LimitClamp.Effective(
            target.HardwareLimits,
            Authority.DeviceLimitsFor(target.Id),
            Authority.CharterLimitsFor(target.Id));

        // 10 & 11. Duty cycle and rate. Sensing consumes neither — a temperature read must not
        //          spend a pump's hourly budget.
        //
        //          Checked across every capability the request will actually move, not just the
        //          id that was asked for. Otherwise `routine.water_cycle` would be a way to run
        //          `act.water_valve` inside its own compiled cooldown, simply because the relay's
        //          history is filed under the relay's name and the routine asked under its own.
        if (target.Class > ActionClass.Observe)
        {
            foreach (var key in target.HistoryKeys)
            {
                if (effectiveLimits.MinOffSeconds is { } minOff &&
                    History.LastEnd(key) is { } lastEnd &&
                    now < lastEnd.AddSeconds(minOff))
                {
                    return KernelDecision.Refuse(RefusalReason.DutyCycle,
                        $"'{key}': min_off_s {Num(minOff)} not elapsed since {lastEnd.ToWire()}", requested);
                }

                if (effectiveLimits.MaxRatePerHour is { } maxRate &&
                    History.StartsInTrailingHour(key, now) >= maxRate)
                {
                    return KernelDecision.Refuse(RefusalReason.RateLimit,
                        $"'{key}': max_rate_per_h {Num(maxRate)} already reached in the trailing hour", requested);
                }
            }
        }

        // 12. Clamp rather than refuse, and record what narrowed — a silent clamp is a lie about
        //     what the mound did.
        var effective = ComputeEffective(requested, target, effectiveLimits, out var clamped, out var clampDetail);

        // 13. Something has to actually be able to do it.
        if (!_executors.ContainsKey(target.Id))
            return KernelDecision.Refuse(RefusalReason.ExecutorMissing,
                $"'{target.Id}' is authorized but no executor is bound to it", requested);

        double? duration = target.DurationParameter is { } durationName &&
                           effective.TryGetValue(durationName, out var durationValue)
            ? durationValue
            : null;

        return new KernelDecision
        {
            Authorized = true,
            Detail = clampDetail,
            RequestedParameters = requested,
            EffectiveParameters = effective,
            Clamped = clamped,
            EffectiveLimits = effectiveLimits,
            RequiredClass = target.Class,
            EvidenceRequired = EvidenceGate.RequiresEvidence(Authority.EffectiveEvidencePolicy(), target.Id),
            EffectiveDurationSeconds = duration,
            HistoryKeys = target.HistoryKeys
        };
    }

    // ---------------------------------------------------------------------------------------
    // Execution
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Authorize, then perform, then verify. Always returns an <see cref="ActionRecord"/> — there
    /// is no path through this method that produces nothing, because SAFETY.md forbids silent
    /// failure and a refusal that never became a record is the silent kind.
    ///
    /// For a routine invocation the record carries the routine id in BOTH <c>capability</c> and
    /// <c>routine_id</c>: evidence policies pattern-match on <c>capability</c>, so a
    /// <c>routine.*</c> policy would not otherwise see it.
    /// </summary>
    public ActionRecord Execute(CapabilityRequest request, DateTimeOffset now,
        IEvidenceLookup? evidence = null)
    {
        var decision = Authorize(request, now);
        var record = NewRecord(request, decision, now);

        if (!decision.Authorized)
        {
            record.Outcome = decision.Refusal == RefusalReason.Stopped
                ? ActionOutcomes.Stopped
                : ActionOutcomes.Refused;
            record.Detail = decision.DescribeRefusal();
            record.EndedAt = now.ToWire();
            return record;
        }

        var execution = new CapabilityExecution
        {
            CapabilityId = request.Capability,
            RoutineId = record.RoutineId,
            Parameters = decision.EffectiveParameters,
            StartedAt = now,
            EffectiveLimits = decision.EffectiveLimits,
            MissionId = request.MissionId
        };

        ExecutionOutcome outcome;
        try
        {
            outcome = _executors[request.Capability].Execute(execution);
        }
        catch (Exception ex)
        {
            // A driver that throws must not take the runtime down with it, and must not vanish.
            // This is a backstop, not a supported style — drivers return Fault.
            outcome = ExecutionOutcome.Fault($"{ex.GetType().Name}: {ex.Message}");
        }

        var endedAt = outcome.EndedAt
                      ?? now.AddSeconds(decision.EffectiveDurationSeconds ?? 0);
        record.EndedAt = endedAt.ToWire();

        // Hardware was touched either way, so the duty cycle applies either way. A driver fault
        // that reset the cooldown would let a caller retry a failing pump without limit.
        //
        // Recorded against every capability the work moved, so a routine's run shows up in the
        // history of the relay it operated and not only under the routine's own name.
        if (decision.RequiredClass > ActionClass.Observe)
            foreach (var key in decision.HistoryKeys)
                History.Record(key, now, endedAt);

        if (!outcome.Succeeded)
        {
            record.Outcome = ActionOutcomes.Failed;
            record.Detail = $"{RefusalReasons.ToWire(RefusalReason.DriverFault)}: {outcome.Detail}";
            return record;
        }

        record.Outcome = decision.Clamped ? ActionOutcomes.Clamped : ActionOutcomes.Succeeded;
        if (decision.Clamped) record.Detail = decision.Detail;

        foreach (var item in outcome.Evidence) record.EvidenceRefs.Add(item.EvidenceId);

        // "Commands are not evidence": the optimistic outcome survives only if something
        // independent actually observed the work.
        var view = BuildEvidenceView(outcome.Evidence, record.EvidenceRefs, evidence);
        var gated = EvidenceGate.Gate(record, Authority.EffectiveEvidencePolicy(), view, now, out var reason);
        if (!string.Equals(gated, record.Outcome, StringComparison.Ordinal))
        {
            // Append rather than replace. A clamped action demoted to `unverified` must still name
            // the limit that narrowed it — SAFETY.md requires the clamp to carry its reason, and
            // losing it because a sensor also failed would hide two facts behind one.
            record.Outcome = gated;
            record.Detail = string.IsNullOrEmpty(record.Detail) ? reason : $"{record.Detail}; {reason}";
        }

        return record;
    }

    // ---------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------

    private ActionRecord NewRecord(CapabilityRequest request, KernelDecision decision, DateTimeOffset now) => new()
    {
        ActionId = Guid.NewGuid().ToString(),
        MissionId = request.MissionId,
        CharterId = Authority.ActiveCharter?.CharterId ?? "",
        Capability = request.Capability,
        RoutineId = CapabilityId.IsRoutine(request.Capability) ? request.Capability : "",
        RequestedParameters = new Dictionary<string, double>(decision.RequestedParameters, StringComparer.Ordinal),
        Parameters = new Dictionary<string, double>(decision.EffectiveParameters, StringComparer.Ordinal),
        StartedAt = now.ToWire(),
        EndedAt = now.ToWire(),
        // Computed from the policy rather than taken from the decision, so it is correct on
        // refusal records too. The field exists to let a reader tell "no evidence was required"
        // from "evidence was required and is missing", and a refusal that always reported false
        // would collapse exactly that distinction.
        EvidenceRequired = EvidenceGate.RequiresEvidence(Authority.EffectiveEvidencePolicy(), request.Capability)
    };

    private ResolvedTarget? Resolve(string id, IReadOnlyDictionary<string, double> requested,
        out KernelDecision? failure)
    {
        failure = null;

        if (CapabilityId.IsRoutine(id))
        {
            if (!Routines.TryGet(id, out var routine))
            {
                failure = KernelDecision.Refuse(RefusalReason.RoutineNotRegistered,
                    $"no routine '{id}' is registered in this build", requested);
                return null;
            }

            if (!routine.Available)
            {
                failure = KernelDecision.Refuse(RefusalReason.CapabilityUnavailable,
                    $"routine '{id}' reports itself unavailable", requested);
                return null;
            }

            // A routine is only as available — and only as permissive — as the capabilities it
            // drives. Its own compiled limits are intersected with theirs, so declaring a routine
            // with a generous bound cannot loosen the relay underneath it.
            var routineLimits = routine.HardwareLimits;
            var historyKeys = new List<string> { routine.Id };

            foreach (var backing in routine.RequiredCapabilities)
            {
                if (!Capabilities.TryGet(backing, out var descriptor))
                {
                    failure = KernelDecision.Refuse(RefusalReason.UnknownCapability,
                        $"routine '{id}' drives '{backing}', which is not registered", requested);
                    return null;
                }

                if (!descriptor.Available)
                {
                    failure = KernelDecision.Refuse(RefusalReason.CapabilityUnavailable,
                        $"routine '{id}' drives '{backing}', which reports itself unavailable", requested);
                    return null;
                }

                routineLimits = LimitClamp.Intersect(descriptor.HardwareLimits, routineLimits);
                historyKeys.Add(backing);
            }

            return new ResolvedTarget
            {
                Id = routine.Id,
                IsRoutine = true,
                Class = routine.Class,
                HardwareLimits = routineLimits,
                Parameters = routine.Parameters,
                RequiredParameters = routine.RequiredParameters,
                ParameterRanges = routine.ParameterRanges,
                DurationParameter = routine.DurationParameter,
                MagnitudeParameter = routine.MagnitudeParameter,
                HistoryKeys = historyKeys
            };
        }

        if (!Capabilities.TryGet(id, out var capability))
        {
            failure = KernelDecision.Refuse(RefusalReason.UnknownCapability,
                CapabilityId.IsWellFormed(id)
                    ? $"'{id}' is not a capability this mound registers"
                    : $"'{id}' is not a well-formed capability id",
                requested);
            return null;
        }

        if (!capability.Available)
        {
            failure = KernelDecision.Refuse(RefusalReason.CapabilityUnavailable,
                $"'{id}' reports itself unavailable", requested);
            return null;
        }

        return new ResolvedTarget
        {
            Id = capability.Id,
            IsRoutine = false,
            Class = capability.Class,
            HardwareLimits = capability.HardwareLimits,
            Parameters = capability.Parameters,
            RequiredParameters = capability.RequiredParameters,
            ParameterRanges = capability.ParameterRanges,
            DurationParameter = capability.DurationParameter,
            MagnitudeParameter = capability.MagnitudeParameter,
            HistoryKeys = [capability.Id]
        };
    }

    private static Dictionary<string, double> ComputeEffective(
        IReadOnlyDictionary<string, double> requested,
        ResolvedTarget target,
        CapabilityLimits effectiveLimits,
        out bool clamped,
        out string detail)
    {
        var effective = new Dictionary<string, double>(StringComparer.Ordinal);
        var notes = new List<string>();

        foreach (var (name, value) in requested)
        {
            var allowed = value;

            // Driver range first: it is the innermost thing that knows what the hardware accepts.
            if (target.ParameterRanges.TryGetValue(name, out var range) && !range.Contains(allowed))
            {
                var narrowed = range.Clamp(allowed);
                notes.Add($"'{name}' {Num(allowed)} -> {Num(narrowed)} by driver range " +
                          $"[{Num(range.Min)}, {Num(range.Max)}]");
                allowed = narrowed;
            }

            if (string.Equals(name, target.DurationParameter, StringComparison.Ordinal) &&
                LimitClamp.ClampOnSeconds(allowed, effectiveLimits, out var byDuration))
            {
                notes.Add($"'{name}' {Num(allowed)} -> {Num(byDuration)} by max_on_s " +
                          $"{Num(effectiveLimits.MaxOnSeconds)}");
                allowed = byDuration;
            }

            if (string.Equals(name, target.MagnitudeParameter, StringComparison.Ordinal) &&
                LimitClamp.ClampToRange(allowed, effectiveLimits, out var byMagnitude))
            {
                notes.Add($"'{name}' {Num(allowed)} -> {Num(byMagnitude)} by " +
                          $"[{Num(effectiveLimits.Min)}, {Num(effectiveLimits.Max)}]");
                allowed = byMagnitude;
            }

            effective[name] = allowed;
        }

        clamped = notes.Count > 0;
        detail = clamped ? string.Join("; ", notes) : "";
        return effective;
    }

    private static Dictionary<string, EvidenceItem> BuildEvidenceView(
        IReadOnlyList<EvidenceItem> produced,
        IReadOnlyList<string> referenced,
        IEvidenceLookup? lookup)
    {
        var view = new Dictionary<string, EvidenceItem>(StringComparer.Ordinal);
        foreach (var item in produced) view[item.EvidenceId] = item;

        if (lookup is null) return view;

        foreach (var id in referenced)
            if (!view.ContainsKey(id) && lookup.TryGet(id, out var item))
                view[id] = item;

        return view;
    }

    /// <summary>
    /// Invariant number formatting. These strings go on the wire inside `detail`, and a decimal
    /// comma from a European locale would make an audit trail unreadable in the colony that
    /// receives it.
    /// </summary>
    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Num(double? value) =>
        value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "unset";

    private sealed class ResolvedTarget
    {
        public required string Id { get; init; }
        public required bool IsRoutine { get; init; }
        public required ActionClass Class { get; init; }
        public required CapabilityLimits HardwareLimits { get; init; }
        public required IReadOnlySet<string> Parameters { get; init; }
        public required IReadOnlySet<string> RequiredParameters { get; init; }
        public required IReadOnlyDictionary<string, ParameterRange> ParameterRanges { get; init; }
        public required IReadOnlyList<string> HistoryKeys { get; init; }
        public string? DurationParameter { get; init; }
        public string? MagnitudeParameter { get; init; }
    }
}
