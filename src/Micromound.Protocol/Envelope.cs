using System.Security.Cryptography;
using System.Text;
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

/// <summary>Shared serializer options — snake_case via explicit JsonPropertyName, no surprises.</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null, // names are explicit on every contract
        WriteIndented = false
    };
}
