using System.Text.Json.Serialization;

namespace Micromound.Protocol;

/// <summary>Closed set of acknowledgement statuses — PROTOCOL.md §2 and §7.</summary>
public static class AckStatuses
{
    /// <summary>The envelope was verified, processed, and is covered by <c>through_seq</c>.</summary>
    public const string Ok = "ok";

    /// <summary>
    /// The envelope was verified and understood, and its content was refused — an invalid
    /// charter, an inapplicable manifest. Machine-distinguishable from success on purpose:
    /// a refusal that only a human reading <c>detail</c> could notice is the silent kind.
    /// </summary>
    public const string Refused = "refused";

    /// <summary>The envelope's kind is not one this endpoint speaks. Refusal is loud, never silent.</summary>
    public const string RefusedUnknownKind = "refused_unknown_kind";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Ok, Refused, RefusedUnknownKind
    };
}

/// <summary>
/// The typed body of an <c>ack</c> envelope.
///
/// This is the message that lets a mound let go of its records. Until an ack covers a sequence
/// number, the uplink queue must retain the envelope and the evidence store must retain the
/// proof — PROTOCOL.md §6's retention rule is written in terms of exactly this message. An ack
/// is therefore additive to the protocol (nothing pinned it before; no golden fixture changes)
/// but load-bearing for storage on both sides.
///
/// <see cref="ThroughSeq"/> is cumulative — "everything up to and including" — rather than a list
/// of individual sequence numbers, because a constrained controller acking a week-long backlog
/// must not need to enumerate it.
/// </summary>
public sealed class AckBody
{
    /// <summary>ok | refused_unknown_kind — see <see cref="AckStatuses"/>.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = AckStatuses.Ok;

    /// <summary>Envelope id this ack answers. Empty on a purely cumulative ack.</summary>
    [JsonPropertyName("refers_to")] public string RefersTo { get; set; } = "";

    /// <summary>
    /// Uplink sequence acknowledged, cumulative and inclusive. Negative means "acknowledges
    /// nothing" — a refusal ack does not advance the window, because acknowledging an envelope
    /// that was refused would tell the sender to discard something nobody processed.
    /// </summary>
    [JsonPropertyName("through_seq")] public long ThroughSeq { get; set; } = -1;

    /// <summary>Evidence ids received and stored, now safe to evict on the device under pressure.</summary>
    [JsonPropertyName("evidence_ids")] public List<string> EvidenceIds { get; set; } = [];

    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
}
