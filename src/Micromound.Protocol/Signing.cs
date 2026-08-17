namespace Micromound.Protocol;

/// <summary>
/// Well-known key identifiers. Uplink envelopes are verified against the sending mound's
/// device key (keyId == mound_id); downlink envelopes against the upstream controller's key.
///
/// The controller is whatever authority signs this mound's charters — ANTHILL's Primary Colony
/// in the reference integration, a bare issuer CLI in a standalone deployment. The protocol does
/// not care which, and deliberately does not name one.
/// </summary>
public static class KeyIds
{
    public const string Controller = "controller";
}

/// <summary>
/// Why a signature was accepted or refused — PROTOCOL.md §2. Refusal is loud and specific:
/// "dropped and audited" is only auditable if the reason survives.
/// </summary>
public enum SignatureStatus
{
    Valid = 0,
    Missing,
    MalformedFormat,
    UnsupportedAlgorithm,
    UnknownKey,
    BadSignature
}

public readonly record struct SignatureCheck(SignatureStatus Status, string Detail)
{
    public bool IsValid => Status == SignatureStatus.Valid;

    public static SignatureCheck Ok() => new(SignatureStatus.Valid, "");

    public static SignatureCheck Refused(SignatureStatus status, string detail) => new(status, detail);

    public string Describe() => IsValid
        ? "signature valid"
        : $"signature_refused: {Status.ToString().ToLowerInvariant()}" +
          (string.IsNullOrEmpty(Detail) ? "" : $" ({Detail})");
}

/// <summary>
/// The `sig` string format: "&lt;algorithm&gt;:&lt;lowercase hex&gt;", e.g. "ed25519:9f3c…".
/// Deliberately parseable by the ESP32 mirror without a JSON or base64 decoder.
/// </summary>
public static class SignatureFormat
{
    public const string Ed25519 = "ed25519";

    public static string Encode(string algorithm, byte[] signature) =>
        algorithm + ":" + Convert.ToHexStringLower(signature);

    public static bool TryDecode(string value, out string algorithm, out byte[] signature)
    {
        algorithm = "";
        signature = [];
        if (string.IsNullOrWhiteSpace(value)) return false;

        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1) return false;

        algorithm = value[..separator];
        try
        {
            signature = Convert.FromHexString(value[(separator + 1)..]);
        }
        catch (FormatException)
        {
            signature = [];
            return false;
        }

        return true;
    }
}

/// <summary>
/// Produces the `sig` for an envelope's canonical bytes. Implementations live outside the
/// protocol library (see Micromound.Crypto) so the wire contracts carry no crypto dependency.
/// </summary>
public interface IEnvelopeSigner
{
    string Algorithm { get; }

    /// <summary>Identifier the counterparty will look this signer's public key up under.</summary>
    string KeyId { get; }

    /// <summary>Returns the encoded signature string for <see cref="Envelope.CanonicalBytes"/>.</summary>
    string Sign(byte[] canonicalBytes);
}

/// <summary>Public keys known to the verifier: mound device keys plus the colony key.</summary>
public interface IPublicKeyDirectory
{
    bool TryGetPublicKey(string keyId, out byte[] publicKey);
}

public interface IEnvelopeVerifier
{
    string Algorithm { get; }

    SignatureCheck Verify(string keyId, byte[] canonicalBytes, string signature);
}

public sealed class InMemoryPublicKeyDirectory : IPublicKeyDirectory
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public void Register(string keyId, byte[] publicKey) => _keys[keyId] = publicKey;

    /// <summary>Re-enrollment is operator-driven (PROTOCOL.md §3); there is no self-service re-key.</summary>
    public bool Revoke(string keyId) => _keys.Remove(keyId);

    public bool TryGetPublicKey(string keyId, out byte[] publicKey)
    {
        if (_keys.TryGetValue(keyId, out var found))
        {
            publicKey = found;
            return true;
        }

        publicKey = [];
        return false;
    }
}

/// <summary>
/// Signing and verification against an envelope's canonical bytes. Note that `sig` is excluded
/// from those bytes, so signing does not disturb the hash chain and the chain does not cover the
/// signature — an envelope with a tampered `sig` fails verification, not chain validation, and
/// both are checked.
/// </summary>
public static class EnvelopeSigning
{
    public static Envelope Sign(Envelope envelope, IEnvelopeSigner signer)
    {
        envelope.Signature = signer.Sign(envelope.CanonicalBytes());
        return envelope;
    }

    public static SignatureCheck Verify(Envelope envelope, IEnvelopeVerifier verifier, string keyId) =>
        verifier.Verify(keyId, envelope.CanonicalBytes(), envelope.Signature);
}
