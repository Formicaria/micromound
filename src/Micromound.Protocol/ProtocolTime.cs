using System.Globalization;

namespace Micromound.Protocol;

/// <summary>
/// The one timestamp format on the wire — PROTOCOL.md §2: <c>2026-08-14T21:04:11Z</c>.
///
/// Fixed width, UTC, second precision, no offset arithmetic and no fractional digits. That is
/// twenty bytes an ESP32 can format with a single <c>snprintf</c> and compare with
/// <c>memcmp</c>, which matters more than it looks: these strings are inside the canonical bytes
/// that get signed and hashed, so a mirror that formats them even slightly differently produces
/// different digests for identical data.
///
/// Second precision is sufficient by construction — every interval the protocol reasons about
/// (<c>lease_ttl_s</c>, <c>min_off_s</c>, <c>min_interval_s</c>, <c>sync_interval_s</c>) is
/// denominated in seconds. If sub-second evidence timing is ever needed, that is a protocol
/// version bump under §10, not a formatting tweak.
/// </summary>
public static class ProtocolTime
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Render an instant in wire form. Non-UTC inputs are converted, never rejected.</summary>
    public static string ToWire(this DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parse a wire timestamp. Deliberately tolerant on the way in — a mound built against an
    /// older library may still send an offset form, and refusing to read it would turn a
    /// cosmetic difference into an unreachable device. Strict on the way out, tolerant on the
    /// way in.
    /// </summary>
    public static bool TryParse(string? value, out DateTimeOffset parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed);
    }

    /// <summary>True when the value is exactly the canonical form, not merely parseable.</summary>
    public static bool IsCanonical(string? value) =>
        value is not null &&
        DateTimeOffset.TryParseExact(value, Format, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _);
}
