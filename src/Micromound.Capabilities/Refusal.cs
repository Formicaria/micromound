namespace Micromound.Capabilities;

/// <summary>
/// Why the kernel refused — SAFETY.md "Prohibited by construction: silent failure".
///
/// A closed enum rather than a string, and specific rather than a bool, for the same reason
/// <c>SignatureStatus</c> is: an operator staring at a mound that will not water the greenhouse
/// needs to know whether the charter is missing, the lease has run out, or the relay simply
/// cooled down for another four minutes. "Refused" alone sends them to a logging system that
/// never captured the answer.
/// </summary>
public enum RefusalReason
{
    /// <summary>A stop order is in force. Precedes every other check, needs no charter.</summary>
    Stopped,
    /// <summary>The id is malformed or names nothing this mound registers.</summary>
    UnknownCapability,
    /// <summary>Registered, but its driver reports itself unavailable or faulted.</summary>
    CapabilityUnavailable,
    /// <summary>No charter is active, so authority is `observe` only.</summary>
    NoCharter,
    /// <summary>The lease has run down; the mound is quiesced and awaiting fresh authority.</summary>
    LeaseExpired,
    /// <summary>Charter is valid but does not grant this capability.</summary>
    NotGranted,
    /// <summary>No routine with that id is registered in this build.</summary>
    RoutineNotRegistered,
    /// <summary>The routine exists but the charter does not enable it.</summary>
    RoutineNotEnabled,
    /// <summary>The action's class sits above the charter's ceiling, or above the worker's own.</summary>
    ActionClassExceeded,
    /// <summary>Hazardous work has no per-action authorization pipeline yet, so it is refused unconditionally.</summary>
    HazardousProhibited,
    /// <summary>A parameter the capability requires was not supplied.</summary>
    MissingParameter,
    /// <summary>A parameter was supplied that this capability does not accept.</summary>
    UnknownParameter,
    /// <summary>`min_off_s` has not elapsed since this capability last ran.</summary>
    DutyCycle,
    /// <summary>`max_rate_per_h` has already been reached in the trailing hour.</summary>
    RateLimit,
    /// <summary>Authorized, but nothing is wired up to actually perform it.</summary>
    ExecutorMissing,
    /// <summary>The driver refused or faulted at execution time.</summary>
    DriverFault
}

public static class RefusalReasons
{
    /// <summary>Stable snake_case wire form, so a refusal survives the trip to the controller.</summary>
    public static string ToWire(RefusalReason reason) => reason switch
    {
        RefusalReason.Stopped => "stopped",
        RefusalReason.UnknownCapability => "unknown_capability",
        RefusalReason.CapabilityUnavailable => "capability_unavailable",
        RefusalReason.NoCharter => "no_charter",
        RefusalReason.LeaseExpired => "lease_expired",
        RefusalReason.NotGranted => "not_granted",
        RefusalReason.RoutineNotRegistered => "routine_not_registered",
        RefusalReason.RoutineNotEnabled => "routine_not_enabled",
        RefusalReason.ActionClassExceeded => "action_class_exceeded",
        RefusalReason.HazardousProhibited => "hazardous_prohibited",
        RefusalReason.MissingParameter => "missing_parameter",
        RefusalReason.UnknownParameter => "unknown_parameter",
        RefusalReason.DutyCycle => "duty_cycle",
        RefusalReason.RateLimit => "rate_limit",
        RefusalReason.ExecutorMissing => "executor_missing",
        RefusalReason.DriverFault => "driver_fault",
        _ => "refused"
    };
}
