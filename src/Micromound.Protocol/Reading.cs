using System.Text.Json.Serialization;
using System.Text.Json;

namespace Micromound.Protocol;

/// <summary>
/// A numeric sensor reading, as it travels inside <see cref="EvidenceItem.PayloadJson"/>.
///
/// This is the missing link between evidence and decisions. <see cref="StepCondition"/> compares
/// an earlier step's reading against a constant, and <see cref="MissionStepResult.Value"/> reports
/// one — but until now <c>payload_json</c> was an opaque string, so nothing could actually produce
/// or read the number those two contracts are written in terms of. A mission could be validated
/// and never executed.
///
/// Deliberately not a new envelope field. <c>payload_json</c> already exists and is already inside
/// the canonical bytes as a string; giving its contents a documented shape adds a convention, not
/// a wire change, so the v0 fixtures frozen at v0.2.1 stay byte-identical. Evidence that is not a
/// number — an image reference, a telemetry window, an outcome code — keeps whatever shape it
/// already had, and <see cref="EvidenceReadings.TryRead"/> simply says no for it.
/// </summary>
public sealed class EvidenceReading
{
    [JsonPropertyName("value")] public double Value { get; set; }

    /// <summary>Unit as the driver reports it: "percent", "celsius", "litres_per_minute".</summary>
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";

    /// <summary>Capability that produced this reading, so a bare item is still self-describing.</summary>
    [JsonPropertyName("capability")] public string Capability { get; set; } = "";
}

/// <summary>
/// Producing and reading <see cref="EvidenceReading"/> payloads.
///
/// Writing is strict — one shape, serialized with <see cref="ProtocolJson.Options"/>, because
/// these bytes end up inside a signed envelope. Reading is tolerant: any payload carrying a
/// numeric <c>value</c> is accepted, whatever else it contains and whatever its
/// <see cref="EvidenceItem.Type"/> says. Strict out, tolerant in — the same rule
/// <see cref="ProtocolTime"/> follows, and for the same reason: a driver built against an older
/// library should not become an unreadable device over a field nobody needed.
/// </summary>
public static class EvidenceReadings
{
    /// <summary>The <see cref="EvidenceItem.Type"/> a numeric reading declares.</summary>
    public const string Type = "reading";

    public static EvidenceItem Create(string evidenceId, string capability, double value,
        DateTimeOffset capturedAt, string unit = "", string source = "") => new()
    {
        EvidenceId = evidenceId,
        Type = Type,
        CapturedAt = capturedAt.ToWire(),
        Source = string.IsNullOrEmpty(source) ? capability : source,
        PayloadJson = JsonSerializer.Serialize(
            new EvidenceReading { Value = value, Unit = unit, Capability = capability },
            ProtocolJson.Options)
    };

    /// <summary>
    /// Read the numeric value out of an evidence item. False when the payload is absent,
    /// unparseable, or carries no numeric <c>value</c> — all three of which mean the same thing
    /// to a caller: this item does not prove a number, so nothing may be decided from it.
    /// </summary>
    public static bool TryRead(EvidenceItem? item, out double value)
    {
        value = 0;
        if (item is null || string.IsNullOrWhiteSpace(item.PayloadJson)) return false;

        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("value", out var property)) return false;
            if (property.ValueKind != JsonValueKind.Number) return false;
            return property.TryGetDouble(out value);
        }
        catch (JsonException)
        {
            // A malformed payload is a reading that does not exist. It is never an exception the
            // runtime has to handle at the call site, because every call site's answer would be
            // the same: treat it as unproven.
            return false;
        }
    }

    /// <summary>The first item in order that carries a number. Order is the caller's, deliberately.</summary>
    public static bool TryReadFirst(IEnumerable<EvidenceItem> items, out double value)
    {
        foreach (var item in items)
            if (TryRead(item, out value))
                return true;

        value = 0;
        return false;
    }
}
