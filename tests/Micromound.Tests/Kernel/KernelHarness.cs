using Micromound.Capabilities;
using Micromound.Protocol;

namespace Micromound.Tests;

/// <summary>
/// A driver stand-in. It counts how many times it was actually reached, which is how these tests
/// prove a refusal refused — rather than merely relabelling an action that already happened.
/// </summary>
internal sealed class FakeExecutor(string capabilityId) : ICapabilityExecutor
{
    public string CapabilityId { get; } = capabilityId;

    public bool IsAvailable { get; set; } = true;

    /// <summary>Return a structured fault instead of succeeding.</summary>
    public bool Faults { get; set; }

    /// <summary>Throw, to prove the kernel contains a badly behaved driver.</summary>
    public bool Throws { get; set; }

    /// <summary>Flip off to simulate a dead sensor: the work happens, nothing observes it.</summary>
    public bool ProducesEvidence { get; set; } = true;

    /// <summary>Backdate produced evidence, for testing staleness.</summary>
    public TimeSpan EvidenceAge { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// When set, produced evidence is a numeric reading (see <see cref="EvidenceReadings"/>)
    /// rather than an opaque window. Mission tests need this: a condition compares an earlier
    /// step's reading, and an opaque payload carries no number to compare.
    /// </summary>
    public double? Reading { get; set; }

    /// <summary>
    /// Receives every produced item, so a test can resolve evidence ids the way a real store
    /// does. Null leaves the executor exactly as it was — kernel tests never needed this.
    /// </summary>
    public Action<EvidenceItem>? Publish { get; set; }

    public int Calls { get; private set; }

    public CapabilityExecution? LastExecution { get; private set; }

    public ExecutionOutcome Execute(CapabilityExecution execution)
    {
        Calls++;
        LastExecution = execution;

        if (Throws) throw new InvalidOperationException("i2c bus timeout");
        if (Faults) return ExecutionOutcome.Fault("relay did not latch");

        var evidence = new List<EvidenceItem>();
        if (ProducesEvidence)
        {
            var capturedAt = execution.StartedAt - EvidenceAge;
            var item = Reading is { } reading
                ? EvidenceReadings.Create($"ev-{CapabilityId}-{Calls}", CapabilityId, reading, capturedAt,
                    source: "fake." + CapabilityId)
                : new EvidenceItem
                {
                    EvidenceId = $"ev-{CapabilityId}-{Calls}",
                    Type = "sensor_window",
                    CapturedAt = capturedAt.ToWire(),
                    Source = "fake." + CapabilityId,
                    PayloadJson = """{"before":0,"after":1}"""
                };

            evidence.Add(item);
            Publish?.Invoke(item);
        }

        return ExecutionOutcome.Ok(evidence);
    }
}

/// <summary>
/// A kernel wired to a small fixed fake device: one sensor, one relay, one routine.
///
/// The hardware limits stand in for what a firmware build would compile in — 30 s maximum
/// on-time, 300 s minimum off-time, 6 actuations an hour — so every "a charter cannot widen
/// this" test has something concrete to fail against.
/// </summary>
internal sealed class KernelHarness
{
    public const string MoundId = "mm-test-001";
    public const string Sensor = "sense.soil_moisture";
    public const string Relay = "act.water_valve";
    public const string WaterCycle = "routine.water_cycle";

    public CapabilityRegistry Capabilities { get; } = new();
    public RoutineRegistry Routines { get; }
    public KernelAuthority Authority { get; } = new(MoundId);
    public CapabilityKernel Kernel { get; }

    public FakeExecutor SensorExecutor { get; } = new(Sensor);
    public FakeExecutor RelayExecutor { get; } = new(Relay);
    public FakeExecutor RoutineExecutor { get; } = new(WaterCycle);

    /// <summary>Everything the fake drivers produced, resolvable by id like a real store.</summary>
    public FakeEvidenceStore Evidence { get; } = new();

    public KernelHarness()
    {
        Routines = new RoutineRegistry(Capabilities);

        MustRegister(Capabilities.Register(new CapabilityDescriptor
        {
            Id = Sensor,
            Class = ActionClass.Observe
        }));

        MustRegister(Capabilities.Register(new CapabilityDescriptor
        {
            Id = Relay,
            Class = ActionClass.Benign,
            HardwareLimits = new CapabilityLimits { MaxOnSeconds = 30, MinOffSeconds = 300, MaxRatePerHour = 6 },
            Parameters = new HashSet<string>(StringComparer.Ordinal) { "on_s" },
            RequiredParameters = new HashSet<string>(StringComparer.Ordinal) { "on_s" },
            DurationParameter = "on_s"
        }));

        MustRegister(Routines.Register(new RoutineDescriptor
        {
            Id = WaterCycle,
            Class = ActionClass.Benign,
            RequiredCapabilities = [Relay],
            HardwareLimits = new CapabilityLimits { MaxOnSeconds = 20 },
            Parameters = new HashSet<string>(StringComparer.Ordinal) { "on_s" },
            DurationParameter = "on_s"
        }));

        SensorExecutor.Publish = Evidence.Add;
        RelayExecutor.Publish = Evidence.Add;
        RoutineExecutor.Publish = Evidence.Add;

        Kernel = new CapabilityKernel(Capabilities, Routines, Authority);
        MustRegister(Kernel.RegisterExecutor(SensorExecutor));
        MustRegister(Kernel.RegisterExecutor(RelayExecutor));
        MustRegister(Kernel.RegisterExecutor(RoutineExecutor));
    }

    /// <summary>Harness setup failing quietly would make every test below meaningless.</summary>
    private static void MustRegister(ValidationResult result)
    {
        if (!result.IsValid)
            throw new InvalidOperationException("harness registration failed: " + string.Join("; ", result.Errors));
    }

    public static DateTimeOffset Now => DateTimeOffset.Parse("2026-08-14T21:00:00Z");

    /// <summary>A valid, permissive charter. Tests narrow it rather than rebuilding one each time.</summary>
    public static Charter NewCharter(DateTimeOffset now, Action<Charter>? adjust = null)
    {
        var charter = new Charter
        {
            CharterId = "c-0001",
            MoundId = MoundId,
            MissionRef = "m-0001",
            IssuedAt = now.ToWire(),
            ExpiresAt = now.AddHours(1).ToWire(),
            LeaseTtlSeconds = 900,
            ActionCeiling = "benign",
            Capabilities = [Sensor, Relay],
            Routines = [WaterCycle],
            Evidence = new EvidencePolicy { RequiredFor = ["act.*", "routine.*"], MinIntervalSeconds = 60 },
            SafeState = "all_actuators_off",
            SyncIntervalSeconds = 15
        };

        adjust?.Invoke(charter);
        return charter;
    }

    public ValidationResult AcceptCharter(DateTimeOffset now, Action<Charter>? adjust = null) =>
        Authority.AcceptCharter(NewCharter(now, adjust), now,
            Capabilities.DeclaredCapabilities(), Routines.DeclaredRoutines());

    public static CapabilityRequest Request(string capability, double? onSeconds = null,
        ActionClass? workerCeiling = null, string worker = "Test Ant")
    {
        var request = new CapabilityRequest
        {
            Capability = capability,
            Worker = worker,
            WorkerCeiling = workerCeiling
        };

        if (onSeconds is { } seconds) request.Parameters["on_s"] = seconds;
        return request;
    }
}

/// <summary>
/// The local evidence store, as far as these tests are concerned: a dictionary that answers the
/// one question <see cref="IEvidenceLookup"/> asks. Micromound.Evidence will own the real one.
/// </summary>
internal sealed class FakeEvidenceStore : IEvidenceLookup
{
    private readonly Dictionary<string, EvidenceItem> _items = new(StringComparer.Ordinal);

    public int Count => _items.Count;

    public void Add(EvidenceItem item) => _items[item.EvidenceId] = item;

    public bool TryGet(string evidenceId, out EvidenceItem item)
    {
        if (_items.TryGetValue(evidenceId, out var found))
        {
            item = found;
            return true;
        }

        item = new EvidenceItem();
        return false;
    }
}
