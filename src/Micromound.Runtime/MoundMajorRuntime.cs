using Micromound.Capabilities;
using Micromound.Protocol;

namespace Micromound.Runtime;

/// <summary>
/// The Mound Major — ARCHITECTURE.md Layer 2, and the deliverable M1 is named for.
///
/// It is a workflow and state-machine coordinator, not an always-running agent: it accepts
/// bounded authority, walks a mission's ordered steps, evaluates deterministic conditions,
/// submits physical work to the capability kernel, and produces a structured outcome.
///
/// What it deliberately does NOT do is decide anything about authority. Every actuation goes
/// through <see cref="CapabilityKernel.Execute"/>, and this class holds no executor, no driver,
/// and no way to reach one. If the coordinator and the kernel ever disagree about whether
/// something may happen, the kernel is the one that decides, because it is the only one asked.
/// That is why the interesting logic here is about ORDER and EVIDENCE rather than permission.
/// </summary>
public sealed class MoundMajor : IMoundMajor
{
    private readonly CapabilityKernel _kernel;
    private readonly IEvidenceLookup? _evidence;
    private readonly Action<ActionRecord>? _recorded;
    private readonly List<ActionRecord> _actions = [];

    /// <param name="kernel">The authority boundary. Not optional, and not replaceable at runtime.</param>
    /// <param name="evidence">
    /// Resolves evidence ids back to items so a sensed value can be read and a condition can be
    /// evaluated. Without it the mound can still act, but every reading is unreadable and every
    /// conditional step refuses — which is the correct failure, loudly, rather than a mission
    /// that quietly treats "I cannot see" as "the condition did not hold".
    /// </param>
    /// <param name="recorded">
    /// Called for every action record a mission produces, refusals included — the hook the
    /// composition root uses to hand records to the Runner Ant's queue. A callback rather than a
    /// queue reference on purpose: the coordinator must not know transport exists, or it would be
    /// one refactor away from consulting connectivity in an authority decision.
    /// </param>
    public MoundMajor(CapabilityKernel kernel, IEvidenceLookup? evidence = null,
        Action<ActionRecord>? recorded = null)
    {
        _kernel = kernel;
        _evidence = evidence;
        _recorded = recorded;
    }

    public string MoundId => _kernel.Authority.MoundId;

    public string State => _kernel.Authority.State;

    public WorkerRegistry Workers { get; } = new();

    /// <summary>Every action this mound has taken, in order — the local half of the audit trail.</summary>
    public IReadOnlyList<ActionRecord> Actions => _actions;

    /// <summary>
    /// Advisory notes from the last accepted charter: limits it tried to widen, which the kernel
    /// intersected away. Not refusals, but SAFETY.md forbids silent anything and a charter author
    /// who believes they granted a 600-second run on a 30-second relay should be told.
    /// </summary>
    public IReadOnlyList<string> CharterNotes { get; private set; } = [];

    public ValidationResult AcceptCharter(Charter charter, DateTimeOffset now)
    {
        // Validated against what this device actually has, not merely against the schema: a
        // charter granting a capability no driver here provides is a promise nobody can keep.
        var result = _kernel.Authority.AcceptCharter(charter, now,
            _kernel.Capabilities.DeclaredCapabilities(),
            _kernel.Routines.DeclaredRoutines());

        CharterNotes = result.IsValid ? _kernel.ReviewCharter(charter).Errors : [];
        return result;
    }

    public ValidationResult ApplyManifest(MoundManifest manifest, DateTimeOffset now)
    {
        var result = ManifestValidator.Validate(manifest, MoundId);
        if (!result.IsValid) return result;   // fails closed: the previous manifest stays in force

        _kernel.Authority.ApplyManifest(manifest);
        return result;
    }

    public void RenewLease(DateTimeOffset now) => _kernel.Authority.RenewLease(now);

    public void Stop() => _kernel.Authority.Stop();

    // ---------------------------------------------------------------------------------------
    // Mission execution
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Validate a mission whole, then walk it.
    ///
    /// Two ordering rules carry the weight here.
    ///
    /// **Refused whole, never partially run.** Validation happens before any step, because a
    /// mission that fails halfway leaves physical state nobody planned and there is no
    /// compensating action for a valve that opened.
    ///
    /// **After a halting step the mission stops acting but keeps looking.** When a step is
    /// refused, fails, or is stopped, no later step may actuate — its premise no longer holds.
    /// But later `sense`, `verify` and `report` steps still run, because the most valuable thing
    /// after a partial actuation is an observation of where the physical world was actually left.
    /// Halting outright would throw that away to keep the code simpler.
    /// </summary>
    public MissionReport Execute(Mission mission, DateTimeOffset now)
    {
        var startedAt = now;

        // The lease is checked against the device's own clock before anything else. A mission
        // that arrived while the lease was alive must not run after it expired.
        _kernel.Authority.QuiesceIfExpired(now);

        var report = new MissionReport
        {
            MissionId = mission.MissionId,
            CharterId = mission.CharterId,
            StartedAt = startedAt.ToWire()
        };

        if (_kernel.Authority.IsStopped)
            return Finish(report, MissionStates.Stopped,
                "a stop order is in force; no mission runs until it is explicitly cleared", now);

        if (_kernel.Authority.IsQuiesced)
            return Finish(report, MissionStates.Quiesced,
                "lease expired; the mound is quiesced and awaiting a fresh charter", now);

        if (_kernel.Authority.ActiveCharter is not { } charter)
            return Finish(report, MissionStates.Refused,
                "no active charter; a mission never carries its own authority", now);

        var validation = MissionValidator.Validate(mission, charter, MoundId, now);
        if (!validation.IsValid)
            return Finish(report, MissionStates.Refused, string.Join("; ", validation.Errors), now);

        // The ants that run a mission, resolved once. A mound with none registered still works:
        // the coordinator submits to the kernel directly and the charter's ceiling alone applies.
        var guard = Workers.All.OfType<IGuardAnt>().FirstOrDefault();
        var witness = Workers.All.OfType<IWitnessAnt>().FirstOrDefault();
        var policy = _kernel.Authority.EffectiveEvidencePolicy();

        // What each step actually did, so a later `verify` step can reach back to the action it
        // confirms. Nothing else in the mission needs this; verification is the only thing that
        // looks backwards.
        var actions = new Dictionary<string, ActionRecord>(StringComparer.Ordinal);
        var results = new Dictionary<string, MissionStepResult>(StringComparer.Ordinal);

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var producedTags = new HashSet<string>(StringComparer.Ordinal);
        var dispatched = new List<ActionRecord>();

        // Tags belonging to steps whose condition did not hold. Their evidence is not missing —
        // it was never due. A mission that correctly declines to water dry-checking soil must not
        // be graded `unverified` for failing to produce proof of the watering it rightly skipped.
        var skippedTags = new HashSet<string>(StringComparer.Ordinal);
        var halted = false;
        var sawUnverified = false;

        // The state of the FIRST step that went wrong. Suppressed later steps must not overwrite
        // it: a hardware fault followed by three unattempted actuations is a mission that FAILED,
        // and calling it `refused` because the suppression label happens to outrank `failed`
        // would blame authority for a broken pump.
        string? haltState = null;

        foreach (var step in mission.Steps)
        {
            var result = new MissionStepResult { StepId = step.StepId };
            var actuates = step.Op is MissionStepOps.Act or MissionStepOps.Routine;

            // A stop arriving mid-mission ends the acting, not the looking. PROTOCOL.md §7:
            // "cease actuation now, enter safe_state, keep sensing and syncing" — and §7 also
            // wants the stop acknowledgement to carry a post-stop sensor snapshot, which a mound
            // that downed tools entirely could never take.
            if (_kernel.Authority.IsStopped && actuates)
            {
                result.State = MissionStepStates.Stopped;
                result.Detail = "stop order in force; actuation ceased";
                report.Steps.Add(result);
                halted = true;
                haltState ??= MissionStepStates.Stopped;
                continue;
            }

            // SAFETY.md Layer 1's software watchdog: loss of the runtime's own heartbeat, or an
            // observed safety trip, drops actuation and enters the declared safe state. Engaging
            // the stop is deliberate — recovery then goes through the one path that restores
            // nothing, rather than through whatever failed deciding it is better now.
            //
            // Physically de-energizing the hardware needs drivers, which arrive in M4. Until then
            // "enters the safe state" is enforced by refusing every actuation, which is the half
            // of it this layer can actually guarantee.
            if (actuates && guard is not null)
            {
                guard.Poll(now);
                if (guard.SafeStateRequired)
                {
                    _kernel.Authority.Stop();
                    result.State = MissionStepStates.Stopped;
                    result.Detail = $"guard demanded safe state: {guard.Reason}";
                    report.Steps.Add(result);
                    halted = true;
                    haltState ??= MissionStepStates.Stopped;
                    continue;
                }
            }

            if (step.Condition is { } condition && !Holds(condition, values, result))
            {
                report.Steps.Add(result);

                // A skipped step is normal; an unreadable source is a refusal and halts like one.
                if (result.State == MissionStepStates.Refused)
                {
                    halted = true;
                    haltState ??= MissionStepStates.Refused;
                }
                else if (!string.IsNullOrEmpty(step.EvidenceTag))
                {
                    skippedTags.Add(step.EvidenceTag);
                }

                continue;
            }

            if (halted && actuates)
            {
                result.State = MissionStepStates.Refused;
                result.Detail = "mission halted by an earlier step; no further actuation";
                report.Steps.Add(result);
                continue;
            }

            if (step.Op == MissionStepOps.Report)
            {
                result.State = MissionStepStates.Executed;
                report.Steps.Add(result);
                continue;
            }

            var record = Dispatch(mission, step, now);
            _actions.Add(record);
            dispatched.Add(record);

            result.ActionId = record.ActionId;
            result.Detail = record.Detail;
            foreach (var reference in record.EvidenceRefs) result.EvidenceRefs.Add(reference);

            if (Resolve(record.EvidenceRefs, out var value))
            {
                result.Value = value;
                values[step.StepId] = value;
            }

            if (!string.IsNullOrEmpty(step.EvidenceTag) && record.EvidenceRefs.Count > 0)
                producedTags.Add(step.EvidenceTag);

            actions[step.StepId] = record;

            // The verify step: the only place an action's verdict is ever revisited.
            //
            // ARCHITECTURE.md — "the second sense is not redundancy… without it the outcome is
            // `unverified` no matter what the driver returned". Making that true needs the
            // confirming reading to reach back to the action it confirms, which is what
            // `confirms` names and what this does with it.
            //
            // The Witness gathers and judges; the coordinator records. One writer for the verdict.
            if (step.Op == MissionStepOps.Verify && !string.IsNullOrWhiteSpace(step.Confirms) &&
                witness is not null && actions.TryGetValue(step.Confirms, out var confirmed))
            {
                var outcome = witness.Confirm(confirmed, Items(record.EvidenceRefs), policy, now, out var why);

                if (!string.Equals(outcome, confirmed.Outcome, StringComparison.Ordinal))
                {
                    confirmed.Outcome = outcome;
                    confirmed.Detail = string.IsNullOrEmpty(confirmed.Detail)
                        ? why
                        : $"{confirmed.Detail}; {why}";

                    // The confirmed STEP still ran, and is still `executed`. What changed is what
                    // the mound may claim about its effect, so the mission is what degrades.
                    sawUnverified = true;
                    if (results.TryGetValue(step.Confirms, out var confirmedResult))
                        confirmedResult.Detail = confirmed.Detail;
                }

                result.Detail = $"confirms '{step.Confirms}': {outcome}" +
                                (string.IsNullOrEmpty(why) ? "" : $" ({why})");
            }

            result.State = StateOf(record.Outcome);
            if (record.Outcome == ActionOutcomes.Unverified) sawUnverified = true;
            if (result.State != MissionStepStates.Executed)
            {
                halted = true;
                haltState ??= result.State;
            }

            results[step.StepId] = result;
            report.Steps.Add(result);
        }

        // Records leave the coordinator only after the walk is over, because a `verify` step can
        // demote an earlier record's outcome — a record published at dispatch time would go up
        // claiming a success its own mission later withdrew.
        if (_recorded is not null)
            foreach (var record in dispatched)
                _recorded(record);

        // Evidence the mission promised. Validation proved a step was TAGGED to produce each one;
        // only execution can prove it actually did, and the difference between those two is the
        // whole reason `unverified` exists as an outcome.
        var missing = mission.RequiredEvidence
            .Where(tag => !producedTags.Contains(tag) && !skippedTags.Contains(tag))
            .ToList();

        return Finish(report, Verdict(report, haltState, missing.Count > 0 || sawUnverified),
            missing.Count > 0 ? "required evidence never produced: " + string.Join(", ", missing) : "",
            now);
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Evaluate a step's guard. A condition whose source produced no readable value does NOT
    /// silently become false: "I could not see" and "the threshold was not met" are different
    /// facts, and collapsing them is how a mound skips watering a dry plant and reports success.
    /// </summary>
    private static bool Holds(StepCondition condition, IReadOnlyDictionary<string, double> values,
        MissionStepResult result)
    {
        if (!values.TryGetValue(condition.SourceStep, out var left))
        {
            result.State = MissionStepStates.Refused;
            result.Detail = $"condition reads step '{condition.SourceStep}', which produced no readable value";
            return false;
        }

        if (ConditionOps.Evaluate(left, condition.Op, condition.Value)) return true;

        result.State = MissionStepStates.Skipped;
        result.Detail = $"condition not met: {left} {condition.Op} {condition.Value}";
        return false;
    }

    /// <summary>
    /// Submit one step to the kernel. The worker named here is audit metadata and a ceiling — the
    /// ceiling is the part that bites, because a Scout Ant declared `observe` cannot actuate even
    /// under a charter that would otherwise allow it.
    /// </summary>
    private ActionRecord Dispatch(Mission mission, MissionStep step, DateTimeOffset now)
    {
        var capability = step.Op == MissionStepOps.Routine ? step.RoutineId : step.Capability;
        var (worker, ceiling) = Target(mission, step);

        var request = new CapabilityRequest
        {
            Capability = capability,
            MissionId = mission.MissionId,
            Worker = worker,
            WorkerCeiling = ceiling
        };

        foreach (var (name, value) in step.Parameters) request.Parameters[name] = value;

        var actuates = step.Op is MissionStepOps.Act or MissionStepOps.Routine;

        // A mission may name its worker. If that worker is registered AND is the right kind of
        // ant, it runs the step and stamps its own ceiling. If it is registered but is not — an
        // application ant declared in a manifest with no code behind it yet — the coordinator
        // submits directly under that worker's ceiling rather than quietly substituting a default
        // ant, because substituting one would apply a ceiling the mission never asked for.
        if (!string.IsNullOrWhiteSpace(mission.Worker) && Workers.TryGet(mission.Worker, out var named))
        {
            if (actuates && named is IForagerAnt namedForager) return namedForager.Request(request, now);
            if (!actuates && named is IScoutAnt namedScout) return namedScout.Sense(request, now);
            return _kernel.Execute(request, now, _evidence);
        }

        if (actuates && Workers.All.OfType<IForagerAnt>().FirstOrDefault() is { } forager)
            return forager.Request(request, now);

        if (!actuates && Workers.All.OfType<IScoutAnt>().FirstOrDefault() is { } scout)
            return scout.Sense(request, now);

        return _kernel.Execute(request, now, _evidence);
    }

    /// <summary>
    /// Which ant runs this step. The mission may name a worker; otherwise the op picks the ant
    /// whose job it is — ANTS.md gives sensing to the Scout and requested action to the Forager,
    /// and nothing is gained by letting a mission author rediscover that.
    /// </summary>
    private (string Worker, ActionClass? Ceiling) Target(Mission mission, MissionStep step)
    {
        var fallback = step.Op switch
        {
            MissionStepOps.Act or MissionStepOps.Routine => DefaultAnts.Forager,
            _ => DefaultAnts.Scout
        };

        var name = string.IsNullOrWhiteSpace(mission.Worker) ? fallback : mission.Worker;

        // An unregistered worker gets no ceiling of its own rather than a default one. Inventing
        // a ceiling here would be this class making an authority decision, which is exactly what
        // it must not do; with none supplied the kernel falls back to the charter alone.
        return Workers.TryGet(name, out var registered)
            ? (name, registered.Descriptor.Ceiling)
            : (name, null);
    }

    /// <summary>
    /// Resolve evidence refs to the items themselves. Empty when this coordinator was given no
    /// lookup: a mound that cannot read its own evidence cannot confirm anything with it, and the
    /// fail-closed answer is that the action stays unproven.
    /// </summary>
    private IReadOnlyList<EvidenceItem> Items(IReadOnlyList<string> evidenceRefs)
    {
        if (_evidence is null) return [];

        var items = new List<EvidenceItem>();
        foreach (var reference in evidenceRefs)
            if (_evidence.TryGet(reference, out var item))
                items.Add(item);

        return items;
    }

    private bool Resolve(IReadOnlyList<string> evidenceRefs, out double value)
    {
        value = 0;
        if (_evidence is null) return false;

        foreach (var reference in evidenceRefs)
            if (_evidence.TryGet(reference, out var item) && EvidenceReadings.TryRead(item, out value))
                return true;

        return false;
    }

    private static string StateOf(string outcome) => outcome switch
    {
        ActionOutcomes.Succeeded or ActionOutcomes.Clamped => MissionStepStates.Executed,
        // The work may have happened and nothing proves it. The step ran; the mission is the
        // thing that becomes unverified, not the step.
        ActionOutcomes.Unverified => MissionStepStates.Executed,
        ActionOutcomes.Refused => MissionStepStates.Refused,
        ActionOutcomes.Stopped => MissionStepStates.Stopped,
        _ => MissionStepStates.Failed
    };

    /// <summary>
    /// The mission's end state is caused by the FIRST step that went wrong, not by the worst
    /// label anywhere in the report. Once a mission halts, later actuating steps are marked
    /// refused because they were never attempted — counting those would let a suppression label
    /// outrank the actual cause and blame authority for a broken pump.
    ///
    /// The one exception is a stop, which outranks everything wherever it appears: a stop is an
    /// instruction that arrived, not an outcome that emerged, and SAFETY.md puts its processing
    /// ahead of all other work.
    /// </summary>
    private static string Verdict(MissionReport report, string? haltState, bool unverified)
    {
        if (report.Steps.Any(s => s.State == MissionStepStates.Stopped)) return MissionStates.Stopped;

        return haltState switch
        {
            MissionStepStates.Refused => MissionStates.Refused,
            MissionStepStates.Failed => MissionStates.Failed,
            _ => unverified ? MissionStates.Unverified : MissionStates.Completed
        };
    }

    private static MissionReport Finish(MissionReport report, string state, string detail,
        DateTimeOffset now)
    {
        report.State = state;
        report.EndedAt = now.ToWire();
        if (!string.IsNullOrEmpty(detail))
            report.Detail = string.IsNullOrEmpty(report.Detail) ? detail : $"{report.Detail}; {detail}";
        return report;
    }
}
