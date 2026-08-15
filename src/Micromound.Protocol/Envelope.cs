using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Micromound.Protocol;

/// <summary>
/// Envelope kinds — PROTOCOL.md §2. Unknown kinds are refused loudly, never processed.
/// </summary>
public static class EnvelopeKinds
{
    public const string MoundSync = "mound_sync";
    public const string Charter = "charter";
    public const string Mission = "mission";
    public const string ActionRecord = "action_record";
    public const string EvidenceBundle = "evidence_bundle";
    public const string Stop = "stop";
    public const string Ack = "ack";
    public const string Enroll = "enroll";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        MoundSync, Charter, Mission, ActionRecord, EvidenceBundle, Stop, Ack, Enroll
    };

    /// <summary>Reduced profile for Deterministic Controllers — PROTOCOL.md §8.</summary>
    public static readonly IReadOnlySet<string> ReducedProfile = new HashSet<string>(StringComparer.Ordinal)
    {
        Enroll, MoundSync, Charter, ActionRecord, Stop, Ack
    };
}

/// <summary>
/// One signed protocol message, either direction — PROTOCOL.md §2. `Body` stays a raw JSON
/// document; typed bodies are deserialized only after kind and signature checks pass.
/// </summary>
public sealed class Envelope
{
    [JsonPropertyName("v")] public int Version { get; set; } = ProtocolVersion.Current;
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("mound_id")] public string MoundId { get; set; } = "";
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("sent_at")] public string SentAt { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("body")] public JsonElement Body { get; set; }
    [JsonPropertyName("prev_digest")] public string PrevDigest { get; set; } = "";
    [JsonPropertyName("sig")] public string Signature { get; set; } = "";

    /// <summary>
    /// Canonical bytes covered by the signature and by the next envelope's `prev_digest`:
    /// every field except `sig` itself, serialized with the shared options.
    /// </summary>
    public byte[] CanonicalBytes()
    {
        var clone = new Envelope
        {
            Version = Version, Id = Id, MoundId = MoundId, Seq = Seq, SentAt = SentAt,
            Kind = Kind, Body = Body, PrevDigest = PrevDigest, Signature = ""
        };
        return JsonSerializer.SerializeToUtf8Bytes(clone, ProtocolJson.Options);
    }

    public string Digest() =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(CanonicalBytes()));
}

public static class ProtocolVersion
{
    public const int Current = 0;
}

/// <summary>
/// Shared serializer options — snake_case via explicit JsonPropertyName, no surprises.
///
/// Every setting here is load-bearing for the M3 C mirror, because these bytes are what gets
/// signed and hashed. Two implementations that agree on the data and disagree on the encoding
/// produce different digests, and the disagreement only shows up as an unverifiable device in
/// the field.
/// </summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        // Every field always present, even when null. That looks wasteful on a constrained device
        // and is the opposite: a fixed shape means the firmware encoder never branches on whether
        // an optional field is set, and the golden fixtures pin one layout rather than a family.
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null, // names are explicit on every contract

        // System.Text.Json's default encoder escapes conservatively for HTML contexts: '+' becomes
        // +, '"' inside a string becomes ". Legal JSON, and a trap here — no hand-written
        // C encoder emits those forms, so the mirror's digests would differ for identical data.
        // Relaxed escaping produces the minimal, natural encoding: literal '+', and \" for quotes.
        // "Unsafe" refers only to embedding output in HTML, which a signed wire format never does.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        WriteIndented = false
    };
}
