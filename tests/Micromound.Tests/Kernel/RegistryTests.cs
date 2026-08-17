using Micromound.Capabilities;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// Registration is the last point at which a misconfigured device can be caught cheaply. After
/// this, a wrong action class or an unbacked routine is only visible as an actuation that should
/// not have been allowed.
/// </summary>
public class CapabilityRegistryTests
{
    [Fact]
    public void A_sense_capability_cannot_be_registered_above_observe()
    {
        var registry = new CapabilityRegistry();

        var result = registry.Register(new CapabilityDescriptor
        {
            Id = "sense.temperature",
            Class = ActionClass.Controlled
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("must be class 'observe'"));
        Assert.False(registry.Contains("sense.temperature"));
    }

    [Fact]
    public void Hazardous_cannot_be_registered_at_all_while_its_pipeline_does_not_exist()
    {
        var registry = new CapabilityRegistry();

        var result = registry.Register(new CapabilityDescriptor
        {
            Id = "act.spindle",
            Class = ActionClass.Hazardous
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("hazardous"));
    }

    [Theory]
    [InlineData("sense.soil_moisture", true)]
    [InlineData("act.water_valve", true)]
    [InlineData("routine.water_cycle", true)]
    [InlineData("sense.camera.front", true)]
    [InlineData("Sense.Temperature", false)]
    [InlineData("gpio.17", false)]
    [InlineData("sense", false)]
    [InlineData("sense.", false)]
    [InlineData("sense.soil moisture", false)]
    [InlineData("", false)]
    public void Capability_ids_are_checked_not_trusted(string id, bool expected) =>
        Assert.Equal(expected, CapabilityId.IsWellFormed(id));

    [Fact]
    public void A_routine_id_does_not_belong_in_the_capability_registry()
    {
        var registry = new CapabilityRegistry();

        var result = registry.Register(new CapabilityDescriptor { Id = "routine.dock" });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void A_declared_parameter_range_must_name_a_real_parameter()
    {
        var registry = new CapabilityRegistry();

        var result = registry.Register(new CapabilityDescriptor
        {
            Id = "act.servo",
            Class = ActionClass.Benign,
            Parameters = new HashSet<string>(StringComparer.Ordinal) { "angle" },
            ParameterRanges = new Dictionary<string, ParameterRange>(StringComparer.Ordinal)
            {
                ["degrees"] = new(0, 180)
            }
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("degrees"));
    }
}

public class RoutineRegistryTests
{
    private static CapabilityRegistry Backing()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new CapabilityDescriptor
        {
            Id = "act.water_valve",
            Class = ActionClass.Benign,
            Parameters = new HashSet<string>(StringComparer.Ordinal) { "on_s" },
            DurationParameter = "on_s"
        });
        registry.Register(new CapabilityDescriptor
        {
            Id = "act.spindle_speed",
            Class = ActionClass.Controlled,
            Parameters = new HashSet<string>(StringComparer.Ordinal) { "rpm" }
        });
        return registry;
    }

    [Fact]
    public void A_routine_over_an_unregistered_capability_is_refused()
    {
        var capabilities = Backing();
        var routines = new RoutineRegistry(capabilities);

        var result = routines.Register(new RoutineDescriptor
        {
            Id = "routine.water_cycle",
            RequiredCapabilities = ["act.nonexistent"]
        });

        Assert.False(result.IsValid);
        Assert.False(routines.Contains("routine.water_cycle"));
    }

    [Fact]
    public void A_routine_cannot_be_a_cheaper_route_to_a_higher_class_capability()
    {
        var capabilities = Backing();
        var routines = new RoutineRegistry(capabilities);

        // 'benign' routine driving a 'controlled' capability would launder the action class.
        var result = routines.Register(new RoutineDescriptor
        {
            Id = "routine.mill_part",
            Class = ActionClass.Benign,
            RequiredCapabilities = ["act.spindle_speed"]
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("controlled"));
    }

    [Fact]
    public void A_well_formed_routine_registers()
    {
        var capabilities = Backing();
        var routines = new RoutineRegistry(capabilities);

        var result = routines.Register(new RoutineDescriptor
        {
            Id = "routine.water_cycle",
            Class = ActionClass.Benign,
            RequiredCapabilities = ["act.water_valve"],
            Parameters = new HashSet<string>(StringComparer.Ordinal) { "on_s" },
            DurationParameter = "on_s"
        });

        Assert.True(result.IsValid);
        Assert.True(routines.Contains("routine.water_cycle"));
    }
}

public class CharterReviewTests
{
    [Fact]
    public void A_charter_that_tries_to_widen_hardware_is_flagged_even_though_it_is_already_inert()
    {
        var h = new KernelHarness();
        var charter = KernelHarness.NewCharter(KernelHarness.Now, c =>
            c.Limits[KernelHarness.Relay] = new CapabilityLimits { MaxOnSeconds = 600 });

        var review = h.Kernel.ReviewCharter(charter);

        Assert.False(review.IsValid);
        Assert.Contains(review.Errors, e => e.Contains("widen the hardware bound"));
    }

    [Fact]
    public void A_charter_that_only_narrows_reviews_clean()
    {
        var h = new KernelHarness();
        var charter = KernelHarness.NewCharter(KernelHarness.Now, c =>
            c.Limits[KernelHarness.Relay] = new CapabilityLimits { MaxOnSeconds = 10, MinOffSeconds = 600 });

        Assert.True(h.Kernel.ReviewCharter(charter).IsValid);
    }
}
