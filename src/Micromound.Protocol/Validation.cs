namespace Micromound.Protocol;

/// <summary>
/// Deterministic, fail-closed validation. Every rejection carries its full reason list — loud,
/// never silent. No I/O, no clock reads: callers pass `now` so validation is pure and trivially
/// testable.
/// </summary>
public static class CharterValidator
{
    public static ValidationResult Validate(Charter charter, string expectedMoundId, DateTimeOffset now,
        IReadOnlySet<string>? deviceCapabilities = null, IReadOnlySet<string>? deviceRoutines = null)
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

        if (!ProtocolTime.TryParse(charter.ExpiresAt, out var expires))
            errors.Add($"expires_at unparseable: '{charter.ExpiresAt}'");
        else if (expires <= now)
            errors.Add("charter already expired");

        if (!ProtocolTime.TryParse(charter.IssuedAt, out var issued))
            errors.Add($"issued_at unparseable: '{charter.IssuedAt}'");
        else if (ProtocolTime.TryParse(charter.ExpiresAt, out var exp2) && exp2 <= issued)
            errors.Add("expires_at precedes issued_at");

        if (charter.LeaseTtlSeconds <= 0) errors.Add("lease_ttl_s must be positive");
        if (charter.SyncIntervalSeconds <= 0) errors.Add("sync_interval_s must be positive");
        if (string.IsNullOrWhiteSpace(charter.SafeState)) errors.Add("safe_state missing");

        foreach (var cap in charter.Capabilities)
        {
            // One way to say a thing. A routine listed among capabilities would be a second,
            // silently ineffective way to enable it, and the drafter would never learn otherwise.
            if (CapabilityId.IsRoutine(cap))
                errors.Add($"'{cap}' is a routine and belongs in 'routines', not 'capabilities'");
            else if (deviceCapabilities is not null && !deviceCapabilities.Contains(cap))
                errors.Add($"capability '{cap}' is not physically present on this device");
        }

        // A charter selects from routines that already exist; it cannot define new behaviour.
        if (deviceRoutines is not null)
            foreach (var routine in charter.Routines)
                if (!deviceRoutines.Contains(routine))
                    errors.Add($"routine '{routine}' is not registered on this device");

        // Limits keyed to something the charter never granted are a drafting error, and silently
        // ignoring them is how an operator comes to believe a bound is in force when it is not.
        foreach (var key in charter.Limits.Keys)
            if (!charter.Capabilities.Contains(key) && !charter.Routines.Contains(key))
                errors.Add($"limits key '{key}' matches no granted capability or routine");

        return new ValidationResult(errors);
    }
}

/// <summary>
/// Validates a structured work packet before any of it executes — PROTOCOL.md §9. A mission that
/// references anything outside its charter is refused whole, not partially run: a half-executed
/// mission leaves physical state nobody planned.
/// </summary>
public static class MissionValidator
{
    public static ValidationResult Validate(Mission mission, Charter charter, string expectedMoundId,
        DateTimeOffset now)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(mission.MissionId)) errors.Add("mission_id missing");

        if (!string.Equals(mission.MoundId, expectedMoundId, StringComparison.Ordinal))
            errors.Add($"mound_id mismatch: mission is for '{mission.MoundId}', this mound is '{expectedMoundId}'");

        if (!string.Equals(mission.CharterId, charter.CharterId, StringComparison.Ordinal))
            errors.Add($"mission cites charter '{mission.CharterId}', active charter is '{charter.CharterId}'");

        if (!ProtocolTime.TryParse(mission.ExpiresAt, out var expires))
            errors.Add($"expires_at unparseable: '{mission.ExpiresAt}'");
        else if (expires <= now)
            errors.Add("mission already expired");

        if (mission.Steps.Count == 0) errors.Add("mission has no steps");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < mission.Steps.Count; i++)
        {
            var step = mission.Steps[i];
            var where = string.IsNullOrWhiteSpace(step.StepId) ? $"step[{i}]" : $"step '{step.StepId}'";

            if (string.IsNullOrWhiteSpace(step.StepId)) errors.Add($"{where}: step_id missing");
            else if (!seen.Add(step.StepId)) errors.Add($"{where}: duplicate step_id");

            if (!MissionStepOps.All.Contains(step.Op))
            {
                errors.Add($"{where}: unknown op '{step.Op}'");
                continue;
            }

            switch (step.Op)
            {
                case MissionStepOps.Sense:
                case MissionStepOps.Verify:
                case MissionStepOps.Act:
                    if (string.IsNullOrWhiteSpace(step.Capability))
                        errors.Add($"{where}: op '{step.Op}' requires a capability");
                    else if (!charter.Capabilities.Contains(step.Capability))
                        errors.Add($"{where}: capability '{step.Capability}' is not granted by the charter");
                    break;

                case MissionStepOps.Routine:
                    if (string.IsNullOrWhiteSpace(step.RoutineId))
                        errors.Add($"{where}: op 'routine' requires a routine_id");
                    else if (!charter.Routines.Contains(step.RoutineId))
                        errors.Add($"{where}: routine '{step.RoutineId}' is not enabled by the charter");
                    else if (mission.AllowedRoutines.Count > 0 && !mission.AllowedRoutines.Contains(step.RoutineId))
                        errors.Add($"{where}: routine '{step.RoutineId}' is outside the mission's allowed_routines");
                    break;
            }

            if (step.Condition is not { } condition) continue;

            if (!ConditionOps.All.Contains(condition.Op))
                errors.Add($"{where}: unknown condition op '{condition.Op}'");

            // Forward and self references would make execution order meaningful in a way the
            // packet does not express. A condition may only read a step that has already run.
            var sourceIndex = mission.Steps.FindIndex(s =>
                string.Equals(s.StepId, condition.SourceStep, StringComparison.Ordinal));

            if (sourceIndex < 0)
                errors.Add($"{where}: condition source_step '{condition.SourceStep}' is not a step in this mission");
            else if (sourceIndex >= i)
                errors.Add($"{where}: condition reads step '{condition.SourceStep}', which does not run first");
        }

        foreach (var capability in mission.RequiredCapabilities)
            if (!charter.Capabilities.Contains(capability))
                errors.Add($"required capability '{capability}' is not granted by the charter");

        foreach (var routine in mission.AllowedRoutines)
            if (!charter.Routines.Contains(routine))
                errors.Add($"allowed routine '{routine}' is not enabled by the charter");

        // Evidence the mission promises but no step is tagged to produce.
        var tags = new HashSet<string>(
            mission.Steps.Select(s => s.EvidenceTag).Where(t => !string.IsNullOrWhiteSpace(t)),
            StringComparer.Ordinal);

        foreach (var required in mission.RequiredEvidence)
            if (!tags.Contains(required))
                errors.Add($"required evidence '{required}' is not produced by any step");

        return new ValidationResult(errors);
    }
}

/// <summary>
/// Validates a declarative mound manifest before it is activated — CONFIGURATION.md. Invalid
/// configuration fails closed: the previous manifest stays in force and the refusal is reported.
/// </summary>
public static class ManifestValidator
{
    public static ValidationResult Validate(MoundManifest manifest, string expectedMoundId,
        IReadOnlySet<string>? knownDrivers = null)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.ManifestId)) errors.Add("manifest_id missing");

        if (!string.Equals(manifest.MoundId, expectedMoundId, StringComparison.Ordinal))
            errors.Add($"mound_id mismatch: manifest is for '{manifest.MoundId}', this mound is '{expectedMoundId}'");

        if (!ProtocolTime.TryParse(manifest.IssuedAt, out _))
            errors.Add($"issued_at unparseable: '{manifest.IssuedAt}'");

        if (string.IsNullOrWhiteSpace(manifest.SafeState)) errors.Add("safe_state missing");

        if (!ReasoningModes.All.Contains(manifest.Reasoning.Mode))
            errors.Add($"reasoning.mode unknown: '{manifest.Reasoning.Mode}'");
        else if (manifest.Reasoning.Mode != ReasoningModes.None &&
                 string.IsNullOrWhiteSpace(manifest.Reasoning.Provider))
            errors.Add($"reasoning.mode '{manifest.Reasoning.Mode}' requires a provider");

        foreach (var (name, binding) in manifest.Hardware)
        {
            if (string.IsNullOrWhiteSpace(binding.Driver))
                errors.Add($"hardware '{name}': driver missing");
            else if (knownDrivers is not null && !knownDrivers.Contains(binding.Driver))
                errors.Add($"hardware '{name}': driver '{binding.Driver}' is not available in this build");
        }

        foreach (var capability in manifest.Capabilities)
            if (!CapabilityId.IsWellFormed(capability))
                errors.Add($"capability '{capability}' is not a well-formed capability id");

        foreach (var routine in manifest.Routines)
            if (!CapabilityId.IsRoutine(routine))
                errors.Add($"routine '{routine}' must be in the 'routine.' namespace");

        var workerNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var worker in manifest.Workers)
        {
            if (string.IsNullOrWhiteSpace(worker.Name)) { errors.Add("worker with no name"); continue; }
            if (!workerNames.Add(worker.Name)) errors.Add($"duplicate worker '{worker.Name}'");

            if (!ActionClasses.TryParse(worker.ActionCeiling, out var ceiling))
                errors.Add($"worker '{worker.Name}': action_ceiling unknown '{worker.ActionCeiling}'");
            else if (ceiling == ActionClass.Hazardous)
                errors.Add($"worker '{worker.Name}': action_ceiling 'hazardous' is never configurable");

            if (!OfflineBehaviours.All.Contains(worker.OfflineBehaviour))
                errors.Add($"worker '{worker.Name}': offline_behaviour unknown '{worker.OfflineBehaviour}'");

            if (worker.RequiresReasoning && manifest.Reasoning.Mode == ReasoningModes.None)
                errors.Add($"worker '{worker.Name}' requires reasoning but reasoning.mode is 'none'");

            foreach (var capability in worker.Consumes)
                if (!manifest.Capabilities.Contains(capability) && !manifest.Routines.Contains(capability))
                    errors.Add($"worker '{worker.Name}' consumes '{capability}', which this mound does not declare");
        }

        foreach (var key in manifest.DeviceLimits.Keys)
            if (!manifest.Capabilities.Contains(key) && !manifest.Routines.Contains(key))
                errors.Add($"device_limits key '{key}' matches no declared capability or routine");

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

        if (!ProtocolTime.TryParse(envelope.SentAt, out _))
            errors.Add($"sent_at unparseable: '{envelope.SentAt}'");

        return new ValidationResult(errors);
    }

    /// <summary>
    /// Full check for a received envelope: shape, kind, and signature — PROTOCOL.md §2.
    /// Unsigned or badly signed envelopes are dropped and audited, never processed, so the
    /// signature failure is reported alongside every other reason rather than short-circuiting.
    /// <paramref name="keyId"/> is the sending mound's id for uplink, or
    /// <see cref="KeyIds.Controller"/> for downlink.
    /// </summary>
    public static ValidationResult Validate(Envelope envelope, IEnvelopeVerifier verifier, string keyId,
        bool reducedProfile = false)
    {
        var errors = new List<string>(Validate(envelope, reducedProfile).Errors);

        var check = EnvelopeSigning.Verify(envelope, verifier, keyId);
        if (!check.IsValid) errors.Add(check.Describe());

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

    /// <summary>
    /// Chain validation plus per-envelope signature verification — what the controller runs over a
    /// backlog drained after an offline period. A chain that verifies structurally but carries
    /// one unsigned envelope is still refused.
    /// </summary>
    public static ValidationResult ValidateChain(IReadOnlyList<Envelope> ordered, string anchorDigest,
        IEnvelopeVerifier verifier, string keyId)
    {
        var errors = new List<string>(ValidateChain(ordered, anchorDigest).Errors);

        for (var i = 0; i < ordered.Count; i++)
        {
            var check = EnvelopeSigning.Verify(ordered[i], verifier, keyId);
            if (!check.IsValid)
                errors.Add($"index {i} (seq {ordered[i].Seq}): {check.Describe()}");
        }

        return new ValidationResult(errors);
    }
}

public sealed class ValidationResult(List<string> errors)
{
    public IReadOnlyList<string> Errors { get; } = errors;
    public bool IsValid => Errors.Count == 0;
}
