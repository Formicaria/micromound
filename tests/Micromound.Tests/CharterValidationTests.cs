using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

public class CharterValidationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    private static Charter Valid() => new()
    {
        CharterId = "c1",
        MoundId = "mm-1",
        MissionRef = "m1",
        IssuedAt = Now.AddMinutes(-1).ToString("O"),
        ExpiresAt = Now.AddHours(1).ToString("O"),
        LeaseTtlSeconds = 900,
        ActionCeiling = "benign",
        Capabilities = ["sense.temp"],
        SafeState = "all_actuators_off"
    };

    [Fact]
    public void Valid_charter_passes()
    {
        var result = CharterValidator.Validate(Valid(), "mm-1", Now);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Hazardous_is_never_a_legal_ceiling()
    {
        var charter = Valid();
        charter.ActionCeiling = "hazardous";
        var result = CharterValidator.Validate(charter, "mm-1", Now);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("hazardous"));
    }

    [Fact]
    public void Wrong_mound_id_is_refused()
    {
        var result = CharterValidator.Validate(Valid(), "mm-OTHER", Now);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Expired_charter_is_refused()
    {
        var charter = Valid();
        charter.ExpiresAt = Now.AddMinutes(-5).ToString("O");
        var result = CharterValidator.Validate(charter, "mm-1", Now);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Capability_the_device_lacks_is_refused()
    {
        var charter = Valid();
        charter.Capabilities.Add("act.plasma_cutter");
        var result = CharterValidator.Validate(charter, "mm-1", Now,
            new HashSet<string> { "sense.temp" });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("act.plasma_cutter"));
    }

    [Fact]
    public void Rejections_carry_full_error_lists_never_just_the_first()
    {
        var charter = new Charter(); // everything wrong at once
        var result = CharterValidator.Validate(charter, "mm-1", Now);
        Assert.True(result.Errors.Count >= 4);
    }
}

public class LimitClampTests
{
    [Fact]
    public void Charter_can_narrow_but_never_widen_firmware_limits()
    {
        var firmware = new CapabilityLimits { MaxOnSeconds = 30, MinOffSeconds = 300, Max = 80 };
        var charter = new CapabilityLimits { MaxOnSeconds = 120, MinOffSeconds = 60, Max = 100 };

        var effective = LimitClamp.Intersect(firmware, charter);

        Assert.Equal(30, effective.MaxOnSeconds);   // charter tried 120 — firmware wins
        Assert.Equal(300, effective.MinOffSeconds); // charter tried 60 — firmware wins
        Assert.Equal(80, effective.Max);            // charter tried 100 — firmware wins
    }

    [Fact]
    public void Narrower_charter_limits_do_apply()
    {
        var firmware = new CapabilityLimits { MaxOnSeconds = 30 };
        var charter = new CapabilityLimits { MaxOnSeconds = 5 };
        Assert.Equal(5, LimitClamp.Intersect(firmware, charter).MaxOnSeconds);
    }
}
