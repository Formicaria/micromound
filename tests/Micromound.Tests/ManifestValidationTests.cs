using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// <see cref="ManifestValidator"/> — CONFIGURATION.md.
///
/// The manifest is the middle limit tier: it sits between what the hardware can physically do and
/// what a charter grants. That makes an internally inconsistent manifest more dangerous than it
/// looks — a device limit keyed to a capability the mound never declared is a bound an operator
/// believes is in force and which nothing will ever apply. So the rule here is the same one
/// SAFETY.md states for actuation and for the same reason: nothing silent. Configuration fails
/// closed, the previous manifest stays in force, and the refusal is reported with its cause.
///
/// Registration-time refusal is also deliberate. SAFETY.md Layer 2 wants a misconfigured device
/// to fail at startup rather than at first use, because first use is when something moves.
/// </summary>
public class ManifestValidationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    /// <summary>The greenhouse mound from CONFIGURATION.md: one relay, one probe, one worker.</summary>
    private static MoundManifest Valid() => new()
    {
        ManifestId = "mf1",
        MoundId = "mm-1",
        IssuedAt = Now.ToWire(),
        SafeState = "all_actuators_off",
        Hardware =
        {
            ["irrigation"] = new HardwareBinding
            {
                Driver = "gpio_relay",
                Settings = { ["pin"] = "17" }
            },
            ["probe"] = new HardwareBinding { Driver = "ads1115" }
        },
        Capabilities = ["sense.soil_moisture", "act.water_valve"],
        Routines = ["routine.water_cycle"],
        DeviceLimits =
        {
            ["act.water_valve"] = new CapabilityLimits { MaxOnSeconds = 30, MinOffSeconds = 300 }
        },
        Workers =
        [
            new WorkerDefinition
            {
                Name = "Soil Ant",
                Purpose = "soil moisture observation and watering",
                RuntimeType = "sensor",
                Consumes = ["sense.soil_moisture", "routine.water_cycle"],
                ActionCeiling = "benign",
                OfflineBehaviour = OfflineBehaviours.Continue
            }
        ]
    };

    private static ValidationResult Validate(MoundManifest manifest,
        IReadOnlySet<string>? knownDrivers = null) =>
        ManifestValidator.Validate(manifest, "mm-1", knownDrivers);

    [Fact]
    public void The_documented_manifest_validates()
    {
        var result = Validate(Valid());
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void A_manifest_for_another_mound_is_refused()
    {
        var manifest = Valid();
        manifest.MoundId = "mm-OTHER";

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("mound_id mismatch"));
    }

    [Fact]
    public void A_manifest_with_no_id_is_refused()
    {
        var manifest = Valid();
        manifest.ManifestId = "  ";

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("manifest_id missing"));
    }

    [Fact]
    public void An_unparseable_issued_at_is_refused()
    {
        var manifest = Valid();
        manifest.IssuedAt = "last tuesday";

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("issued_at unparseable"));
    }

    /// <remarks>
    /// Safe state is what the mound de-energizes into when the watchdog trips or the lease runs
    /// out. A manifest that does not declare one has no answer to "and then what", so it cannot
    /// be activated.
    /// </remarks>
    [Fact]
    public void A_manifest_with_no_safe_state_is_refused()
    {
        var manifest = Valid();
        manifest.SafeState = "";

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("safe_state missing"));
    }

    [Fact]
    public void An_unknown_reasoning_mode_is_refused()
    {
        var manifest = Valid();
        manifest.Reasoning.Mode = "autonomous";

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("reasoning.mode unknown"));
    }

    [Theory]
    [InlineData(ReasoningModes.Remote)]
    [InlineData(ReasoningModes.Local)]
    public void Reasoning_that_is_switched_on_without_a_provider_is_refused(string mode)
    {
        var manifest = Valid();
        manifest.Reasoning.Mode = mode;

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("requires a provider"));
    }

    [Fact]
    public void Reasoning_off_is_the_default_and_needs_no_provider()
    {
        Assert.Equal(ReasoningModes.None, new MoundManifest().Reasoning.Mode);
        Assert.True(Validate(Valid()).IsValid);
    }

    [Fact]
    public void A_hardware_binding_with_no_driver_is_refused()
    {
        var manifest = Valid();
        manifest.Hardware["probe"].Driver = "";

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("hardware 'probe'") && e.Contains("driver missing"));
    }

    /// <remarks>
    /// The driver set is passed in rather than looked up, so this check works identically for a
    /// Pi build and for a firmware image with four drivers compiled in.
    /// </remarks>
    [Fact]
    public void A_driver_this_build_does_not_contain_is_refused()
    {
        var result = Validate(Valid(), new HashSet<string> { "gpio_relay" });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ads1115") && e.Contains("not available"));
    }

    [Fact]
    public void Every_declared_driver_present_in_the_build_passes()
    {
        var result = Validate(Valid(), new HashSet<string> { "gpio_relay", "ads1115" });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    /// <remarks>
    /// Capability ids end up in charters, evidence patterns, and firmware tables. A case-folding
    /// or Unicode question in any of those is a question about whether two devices agree on
    /// authority, so the namespace is closed and the character set is ASCII lowercase.
    /// </remarks>
    [Theory]
    [InlineData("temperature")]          // no namespace at all
    [InlineData("telemetry.temp")]       // namespace outside the closed set
    [InlineData("Sense.Temp")]           // uppercase
    [InlineData("sense.")]               // empty segment
    [InlineData("sense.soil-moisture")]  // hyphen is not in [a-z0-9_]
    public void Malformed_capability_ids_are_refused(string id)
    {
        var manifest = Valid();
        manifest.Capabilities.Add(id);

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not a well-formed capability id"));
    }

    [Fact]
    public void A_routine_outside_the_routine_namespace_is_refused()
    {
        var manifest = Valid();
        manifest.Routines.Add("act.water_cycle");

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("must be in the 'routine.' namespace"));
    }

    [Fact]
    public void Duplicate_worker_names_are_refused()
    {
        var manifest = Valid();
        manifest.Workers.Add(new WorkerDefinition { Name = "Soil Ant", ActionCeiling = "observe" });

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("duplicate worker 'Soil Ant'"));
    }

    /// <remarks>
    /// A nameless worker cannot be reported on, so every later message about it would have no
    /// subject. The validator says one thing and moves on — pinned here by giving the nameless
    /// worker a second fault that must NOT be reported.
    /// </remarks>
    [Fact]
    public void A_nameless_worker_is_refused_without_further_inspection()
    {
        var manifest = Valid();
        manifest.Workers.Add(new WorkerDefinition { Name = "", ActionCeiling = "hazardous" });

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("worker with no name", result.Errors[0]);
    }

    [Fact]
    public void An_unknown_worker_action_ceiling_is_refused()
    {
        var manifest = Valid();
        manifest.Workers[0].ActionCeiling = "elevated";

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("action_ceiling unknown"));
    }

    /// <remarks>
    /// SAFETY.md Layer 2: hazardous needs explicit per-action authorization from the controller,
    /// and until that pipeline ships with tests it is refused unconditionally. Configuration is
    /// the tempting back door — it is operator-set, it is local, and it looks like a setting.
    /// It is not a setting.
    /// </remarks>
    [Fact]
    public void Hazardous_is_never_a_configurable_worker_ceiling()
    {
        var manifest = Valid();
        manifest.Workers[0].ActionCeiling = "hazardous";

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("never configurable"));
    }

    /// <remarks>
    /// Closed on purpose: "ant does not mean language model" is only enforceable if a manifest
    /// cannot invent a kind whose meaning nothing agrees on.
    /// </remarks>
    [Fact]
    public void An_unknown_runtime_type_is_refused()
    {
        var manifest = Valid();
        manifest.Workers[0].RuntimeType = "autonomous_agent";

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("runtime_type unknown"));
    }

    /// <remarks>
    /// An undeclared `exposes` entry reads to every other worker as an available capability and
    /// resolves to nothing — the same silent shape as a device limit keyed to nothing.
    /// </remarks>
    [Fact]
    public void A_worker_exposing_something_the_mound_never_declared_is_refused()
    {
        var manifest = Valid();
        manifest.Workers[0].Exposes = ["sense.leaf_wetness"];

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("exposes") && e.Contains("does not declare"));
    }

    [Fact]
    public void An_unknown_offline_behaviour_is_refused()
    {
        var manifest = Valid();
        manifest.Workers[0].OfflineBehaviour = "improvise";

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("offline_behaviour unknown"));
    }

    [Fact]
    public void A_worker_that_needs_reasoning_on_a_mound_with_none_is_refused()
    {
        var manifest = Valid();
        manifest.Workers[0].RequiresReasoning = true;

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("requires reasoning"));
    }

    [Fact]
    public void A_worker_consuming_something_the_mound_never_declared_is_refused()
    {
        var manifest = Valid();
        manifest.Workers[0].Consumes.Add("sense.leaf_wetness");

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("sense.leaf_wetness") && e.Contains("does not declare"));
    }

    /// <summary>
    /// `consumes` spans both namespaces: a worker may request a capability or invoke a routine,
    /// and the manifest declares both, so the check accepts either.
    /// </summary>
    [Fact]
    public void A_worker_may_consume_a_declared_routine_as_well_as_a_capability()
    {
        var manifest = Valid();
        manifest.Workers[0].Consumes = ["routine.water_cycle"];

        var result = Validate(manifest);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    /// <remarks>
    /// The one an operator would never notice: a limit keyed to a capability this mound does not
    /// have is a bound that will never be applied to anything, and silently dropping it is how
    /// somebody comes to believe a pump is capped when it is not.
    /// </remarks>
    [Fact]
    public void Device_limits_keyed_to_nothing_this_mound_declares_are_refused()
    {
        var manifest = Valid();
        manifest.DeviceLimits["act.plasma_cutter"] = new CapabilityLimits { MaxOnSeconds = 5 };

        var result = Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("device_limits key") && e.Contains("act.plasma_cutter"));
    }

    [Fact]
    public void Device_limits_may_be_keyed_to_a_routine()
    {
        var manifest = Valid();
        manifest.DeviceLimits["routine.water_cycle"] = new CapabilityLimits { MaxRatePerHour = 4 };

        var result = Validate(manifest);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Rejections_carry_full_error_lists_never_just_the_first()
    {
        var result = ManifestValidator.Validate(new MoundManifest(), "mm-1");

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 3, string.Join("; ", result.Errors));
    }
}
