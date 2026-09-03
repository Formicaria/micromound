namespace Micromound.Protocol;

/// <summary>
/// The controller-tier vocabulary a mound declares at enrollment (PROTOCOL.md §3.2). This is the ONE
/// place the strings live: the controller (ANTHILL) validates the tier a device presents against
/// exactly this set and refuses an unknown one, so a device that invents its own label never gets
/// through the front door. Both sides of the wire reference this library, which is what keeps the
/// vocabulary from drifting apart.
///
/// <para>The tier describes the <em>class of device</em>, not the coordinator ant that runs on it:
/// every mound is run by a Mound Major, but a Raspberry Pi running the full colony is an
/// <c>edge_queen</c>, and a constrained microcontroller (M5's ESP32) running the reduced,
/// deterministic-only profile is a <c>deterministic_controller</c>.</para>
///
/// <para><b>On the name.</b> The reference controller already declares its own <c>MoundTiers</c> in a
/// namespace it imports alongside this one; a type of that name here would make every unqualified use
/// on its side ambiguous and break its build the next time it compiled against this repository. So
/// this is <c>ControllerTiers</c> — PROTOCOL.md §3's own phrase, "controller tier" — and the two can
/// coexist until the controller retires its copy in favour of this one. Do not rename it back.</para>
/// </summary>
public static class ControllerTiers
{
    /// <summary>A full-colony edge device — a Linux/Pi host running every ant. The usual tier.</summary>
    public const string EdgeQueen = "edge_queen";

    /// <summary>A constrained, deterministic-only subordinate controller (the ESP32 profile).</summary>
    public const string DeterministicController = "deterministic_controller";

    /// <summary>True for a tier the controller will accept. Anything else is refused at enrollment.</summary>
    public static bool IsKnown(string? tier) =>
        tier is EdgeQueen or DeterministicController;
}
