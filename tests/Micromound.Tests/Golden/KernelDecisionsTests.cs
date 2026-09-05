using System.Text;
using System.Text.Json;
using Micromound.Capabilities;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// Freezes the capability kernel's DECISIONS — the thing the C kernel (firmware/micromound-c,
/// mm_kernel) must reproduce, refusal for refusal, clamp for clamp, record for record.
///
/// A fixed device (three capabilities, one routine, device limits), a fixed clock, a scripted
/// sequence of charters, stops and requests. Every step writes what the kernel decided: the
/// refusal reason from the closed set, the detail line, the effective parameters, the mound
/// state afterwards, and the action record's body as it would go on the wire (action ids are
/// normalized to `a-<step>`, since the kernel mints GUIDs). A constrained controller that refused
/// differently from a Pi would make "the mound refused" mean two different things — this file is
/// what makes that a test failure instead of a field incident.
/// </summary>
public class KernelDecisionsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-14T21:04:11Z");
    private const string MoundId = "mm-7f3a0000-0000-4000-8000-000000000001";

    /// <summary>An executor that succeeds, ends when the effective duration says, and produces one reading (taken at the start) unless told not to.</summary>
    private sealed class ScriptedExecutor(string id) : ICapabilityExecutor
    {
        public string CapabilityId { get; } = id;
        public bool IsAvailable { get; set; } = true;
        public bool ProduceEvidence { get; set; } = true;
        public int StaleBySeconds { get; set; }
        public bool Fault { get; set; }
        private int _n;

        public ExecutionOutcome Execute(CapabilityExecution execution)
        {
            if (Fault) return ExecutionOutcome.Fault("relay did not answer");
            var duration = execution.Parameters.TryGetValue("on_s", out var s) ? s : 0;
            var ended = execution.StartedAt.AddSeconds(duration);
            if (!ProduceEvidence) return ExecutionOutcome.Ok([], ended);
            _n++;
            return ExecutionOutcome.Ok(
            [
                new EvidenceItem
                {
                    EvidenceId = $"e-{CapabilityId}-{_n}",
                    Type = "sensor_window",
                    CapturedAt = execution.StartedAt.AddSeconds(-StaleBySeconds).ToWire(),   /* a reading taken as the action starts */
                    Source = "sim." + CapabilityId,
                    PayloadJson = "{}",
                    ContentDigest = ""
                }
            ], ended);
        }
    }

    private static (CapabilityKernel kernel, Dictionary<string, ScriptedExecutor> executors) Device()
    {
        var caps = new CapabilityRegistry();
        Assert.True(caps.Register(new CapabilityDescriptor { Id = "sense.temp", Class = ActionClass.Observe }).IsValid);
        Assert.True(caps.Register(new CapabilityDescriptor
        {
            Id = "act.relay_1", Class = ActionClass.Benign,
            HardwareLimits = new CapabilityLimits { MaxOnSeconds = 60, MinOffSeconds = 120, MaxRatePerHour = 4 },
            Parameters = new HashSet<string> { "on_s" }, RequiredParameters = new HashSet<string> { "on_s" },
            ParameterRanges = new Dictionary<string, ParameterRange> { ["on_s"] = new(1, 3600) },
            DurationParameter = "on_s"
        }).IsValid);
        Assert.True(caps.Register(new CapabilityDescriptor
        {
            Id = "act.dimmer", Class = ActionClass.Controlled,
            HardwareLimits = new CapabilityLimits { Max = 80 },
            Parameters = new HashSet<string> { "level" },
            ParameterRanges = new Dictionary<string, ParameterRange> { ["level"] = new(0, 100) },
            MagnitudeParameter = "level"
        }).IsValid);
        Assert.True(caps.Register(new CapabilityDescriptor { Id = "act.fan", Class = ActionClass.Benign }).IsValid);

        var routines = new RoutineRegistry(caps);
        Assert.True(routines.Register(new RoutineDescriptor
        {
            Id = "routine.cool", Class = ActionClass.Benign, RequiredCapabilities = ["act.relay_1"],
            HardwareLimits = new CapabilityLimits { MaxOnSeconds = 45 },
            Parameters = new HashSet<string> { "on_s" }, DurationParameter = "on_s"
        }).IsValid);

        var authority = new KernelAuthority(MoundId);
        authority.ApplyManifest(new MoundManifest
        {
            MoundId = MoundId,
            DeviceLimits = { ["act.relay_1"] = new CapabilityLimits { MaxOnSeconds = 40 } }
        });

        var kernel = new CapabilityKernel(caps, routines, authority);
        var executors = new Dictionary<string, ScriptedExecutor>();
        foreach (var id in new[] { "sense.temp", "act.relay_1", "act.dimmer", "routine.cool" })
        {
            executors[id] = new ScriptedExecutor(id);
            Assert.True(kernel.RegisterExecutor(executors[id]).IsValid);
        }
        // act.fan is registered but has no executor, on purpose.
        return (kernel, executors);
    }

    private static Charter Charter(string id, string ceiling, int leaseTtl, string[] caps, string[] routines,
        Dictionary<string, CapabilityLimits> limits, string[] requiredFor) => new()
    {
        CharterId = id, MoundId = MoundId, MissionRef = "mission-0001",
        IssuedAt = T0.ToWire(), ExpiresAt = T0.AddHours(3).ToWire(),
        LeaseTtlSeconds = leaseTtl, ActionCeiling = ceiling,
        Capabilities = [.. caps], Routines = [.. routines], Limits = limits,
        Evidence = new EvidencePolicy { RequiredFor = [.. requiredFor], MinIntervalSeconds = 60 },
        SafeState = "all_actuators_off", SyncIntervalSeconds = 15
    };

    [Fact]
    public void Kernel_decisions_are_frozen()
    {
        var (kernel, executors) = Device();
        var report = new StringBuilder();
        var step = 0;

        report.AppendLine("# MICROMOUND kernel decisions — golden fixture");
        report.AppendLine("#");
        report.AppendLine("# Frozen by tests/Micromound.Tests/Golden/KernelDecisionsTests.cs. The C kernel (mm_kernel) runs the same");
        report.AppendLine("# device, clock and script and must reproduce every line: reason, detail, effective parameters, state,");
        report.AppendLine("# and the action record body (action_id normalized to a-<step>).");
        report.AppendLine("#");
        report.AppendLine("# Device: sense.temp (observe); act.relay_1 (benign; hw max_on_s 60, min_off_s 120, max_rate_per_h 4;");
        report.AppendLine("#   on_s required, range [1,3600], duration); act.dimmer (controlled; hw max 80; level range [0,100], magnitude);");
        report.AppendLine("#   act.fan (benign, NO executor); routine.cool (benign; drives act.relay_1; hw max_on_s 45; on_s duration).");
        report.AppendLine("# Device limits: act.relay_1 max_on_s 40. Executors succeed, end at start + on_s, produce one reading.");
        report.AppendLine();

        void Request(string label, DateTimeOffset now, string capability, Dictionary<string, double>? parameters = null,
            ActionClass? workerCeiling = null)
        {
            step++;
            var request = new CapabilityRequest
            {
                Capability = capability, Parameters = parameters ?? [], Worker = workerCeiling is null ? "" : "scout",
                WorkerCeiling = workerCeiling
            };
            var decision = kernel.Authorize(request, now);
            var record = kernel.Execute(request, now);
            record.ActionId = $"a-{step}";

            report.AppendLine($"## step {step} — {label}");
            report.AppendLine($"at:        {now.ToWire()}  (+{(now - T0).TotalSeconds:0}s)");
            report.AppendLine($"request:   {capability} {JsonSerializer.Serialize(request.Parameters, ProtocolJson.Options)}" +
                              (workerCeiling is { } wc ? $" worker_ceiling={ActionClasses.ToWire(wc)}" : ""));
            report.AppendLine($"decision:  {(decision.Authorized ? "authorized" : "refused " + RefusalReasons.ToWire(decision.Refusal!.Value))}");
            report.AppendLine($"detail:    {decision.Detail}");
            report.AppendLine($"effective: {JsonSerializer.Serialize(decision.EffectiveParameters, ProtocolJson.Options)} clamped={(decision.Clamped ? "true" : "false")}" +
                              $" evidence_required={(decision.EvidenceRequired ? "true" : "false")}");
            report.AppendLine($"limits:    {JsonSerializer.Serialize(decision.EffectiveLimits, ProtocolJson.Options)}");
            report.AppendLine($"state:     {kernel.Authority.State}");
            report.AppendLine($"record:    {JsonSerializer.Serialize(record, ProtocolJson.Options)}");
            report.AppendLine();
        }

        void Event(string label, DateTimeOffset now, Func<string> act)
        {
            step++;
            var outcome = act();
            report.AppendLine($"## step {step} — {label}");
            report.AppendLine($"at:        {now.ToWire()}  (+{(now - T0).TotalSeconds:0}s)");
            report.AppendLine($"event:     {outcome}");
            report.AppendLine($"state:     {kernel.Authority.State}");
            report.AppendLine();
        }

        string Accept(Charter c, DateTimeOffset now)
        {
            var result = kernel.Authority.AcceptCharter(c, now, kernel.Capabilities.DeclaredCapabilities(), kernel.Routines.DeclaredRoutines());
            var review = kernel.ReviewCharter(c);
            return (result.IsValid ? "charter accepted" : "charter refused: " + string.Join("; ", result.Errors)) +
                   (review.Errors.Count > 0 ? " | review: " + string.Join("; ", review.Errors) : "");
        }

        var on = (double s) => new Dictionary<string, double> { ["on_s"] = s };

        // --- observe-only ---
        Request("sensing needs no charter", T0, "sense.temp");
        Request("actuation with no charter", T0, "act.relay_1", on(30));
        Request("a routine with no charter", T0, "routine.cool", on(10));

        // --- a benign charter ---
        var charter = Charter("c0000000-0000-4000-8000-000000000001", "benign", 3600,
            ["sense.temp", "act.relay_1", "act.fan"], ["routine.cool"],
            new Dictionary<string, CapabilityLimits> { ["act.relay_1"] = new() { MaxOnSeconds = 30, MinOffSeconds = 300, MaxRatePerHour = 10 } },
            ["act.*"]);
        Event("accept a benign charter (charter tries to widen max_rate_per_h: 10 over hardware 4)", T0, () => Accept(charter, T0));

        Request("clamped by the narrowest tier: charter 30 under device 40 under hardware 60", T0, "act.relay_1", on(50));
        Request("duty cycle: min_off_s 300 not elapsed", T0.AddSeconds(40), "act.relay_1", on(10));
        Request("class exceeded: controlled over a benign ceiling", T0.AddSeconds(41), "act.dimmer", new() { ["level"] = 50 });
        Request("unknown parameter is refused, not dropped", T0.AddSeconds(42), "act.relay_1", new() { ["speed"] = 3 });
        Request("missing required parameter", T0.AddSeconds(43), "act.relay_1");
        Request("granted but nothing can do it", T0.AddSeconds(44), "act.fan");
        Request("unknown capability", T0.AddSeconds(45), "act.nothing");
        Request("malformed capability id", T0.AddSeconds(46), "not-an-id");
        Request("routine not registered", T0.AddSeconds(47), "routine.freeze");
        Request("worker ceiling below the charter's", T0.AddSeconds(48), "act.relay_1", on(5), ActionClass.Observe);
        Request("sensing spends no duty cycle", T0.AddSeconds(49), "sense.temp");
        Request("routine: clamped by its own compiled bound (45) intersected with the relay's (60)", T0.AddSeconds(400), "routine.cool", on(100));
        Request("routine inside the relay's cooldown is refused under the relay's key", T0.AddSeconds(500), "act.relay_1", on(5));

        executors["act.relay_1"].IsAvailable = false;
        Request("driver reports itself unavailable", T0.AddSeconds(800), "act.relay_1", on(5));
        executors["act.relay_1"].IsAvailable = true;

        Request("relay again after the cooldown", T0.AddSeconds(800), "act.relay_1", on(5));
        Request("relay: fourth start in the trailing hour", T0.AddSeconds(1200), "act.relay_1", on(5));
        Request("rate limit: hardware max_rate_per_h 4 reached", T0.AddSeconds(1600), "act.relay_1", on(5));

        Event("renew the lease", T0.AddSeconds(3000), () => { kernel.Authority.RenewLease(T0.AddSeconds(3000)); return "lease renewed"; });

        executors["act.relay_1"].ProduceEvidence = false;
        Request("commands are not evidence: succeeded but demoted to unverified", T0.AddSeconds(4100), "act.relay_1", on(5));
        executors["act.relay_1"].ProduceEvidence = true;
        executors["act.relay_1"].StaleBySeconds = 400;
        Request("stale evidence demotes too (captured 400 s before a 60 s window)", T0.AddSeconds(4500), "act.relay_1", on(5));
        executors["act.relay_1"].StaleBySeconds = 0;
        executors["act.relay_1"].Fault = true;
        Request("a driver fault is failed, and still spends the duty cycle", T0.AddSeconds(4900), "act.relay_1", on(5));
        executors["act.relay_1"].Fault = false;
        Request("the failed run's cooldown holds", T0.AddSeconds(5000), "act.relay_1", on(5));

        // --- lease ---
        Event("the lease runs out", T0.AddSeconds(6700), () => kernel.Authority.QuiesceIfExpired(T0.AddSeconds(6700)) ? "quiesced" : "still alive");
        Request("after expiry: actuation refused", T0.AddSeconds(6701), "act.relay_1", on(5));
        Request("after expiry: sensing continues", T0.AddSeconds(6702), "sense.temp");
        Event("a fresh charter is the only way out of quiesce", T0.AddSeconds(6800), () => Accept(charter, T0.AddSeconds(6800)));
        Request("chartered again", T0.AddSeconds(6801), "act.relay_1", on(5));

        // --- stop ---
        Event("stop", T0.AddSeconds(7000), () => { kernel.Authority.Stop(); return "stopped"; });
        Request("stopped: actuation refused before anything resolves", T0.AddSeconds(7001), "act.nonexistent", on(5));
        Request("stopped: sensing continues", T0.AddSeconds(7002), "sense.temp");
        Event("a charter cannot clear a stop", T0.AddSeconds(7003), () => Accept(charter, T0.AddSeconds(7003)));
        Event("the stop is cleared explicitly", T0.AddSeconds(7004), () => { kernel.Authority.ClearStop(); return "stop cleared"; });
        Request("cleared, but observe-only until chartered", T0.AddSeconds(7005), "act.relay_1", on(5));

        // --- a second charter, narrower ---
        var observe = Charter("c0000000-0000-4000-8000-000000000002", "observe", 600, ["sense.temp"], [],
            new Dictionary<string, CapabilityLimits>(), []);
        Event("an observe-ceiling charter", T0.AddSeconds(7100), () => Accept(observe, T0.AddSeconds(7100)));
        Request("actuation under an observe ceiling", T0.AddSeconds(7101), "act.relay_1", on(5));
        Request("a routine the charter does not enable", T0.AddSeconds(7102), "routine.cool", on(5));
        var expired = Charter("c0000000-0000-4000-8000-000000000003", "benign", 600, ["act.relay_1"], [],
            new Dictionary<string, CapabilityLimits>(), []);
        expired.ExpiresAt = T0.ToWire();
        Event("an expired charter is refused with the validator's reasons", T0.AddSeconds(7200), () => Accept(expired, T0.AddSeconds(7200)));
        var mismatched = Charter("c0000000-0000-4000-8000-000000000004", "benign", 600, ["act.relay_1", "act.laser"], [],
            new Dictionary<string, CapabilityLimits> { ["act.dimmer"] = new() { Max = 10 } }, []);
        Event("a charter naming what this device does not have", T0.AddSeconds(7300), () => Accept(mismatched, T0.AddSeconds(7300)));

        GoldenFile.Verify("kernel-decisions.txt", report.ToString());
    }
}
