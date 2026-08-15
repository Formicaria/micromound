using Micromound.Protocol;

namespace Micromound.Crypto;

/// <summary>
/// Signs envelopes with a device (or colony) Ed25519 key — PROTOCOL.md §2.
/// </summary>
public sealed class Ed25519EnvelopeSigner(string keyId, Ed25519KeyPair keyPair) : IEnvelopeSigner
{
    private readonly Ed25519KeyPair _keyPair = keyPair;

    public string Algorithm => SignatureFormat.Ed25519;

    public string KeyId { get; } = keyId;

    public byte[] PublicKey => _keyPair.PublicKey;

    public string Sign(byte[] canonicalBytes) =>
        SignatureFormat.Encode(SignatureFormat.Ed25519, _keyPair.SignRaw(canonicalBytes));
}

/// <summary>
/// Verifies envelope signatures against a directory of known public keys. There is no unsigned
/// mode and no "trust on first use": a key the directory does not hold is a refusal, because
/// enrollment (PROTOCOL.md §3) is the only way a key gets bound to a mound.
/// </summary>
public sealed class Ed25519EnvelopeVerifier(IPublicKeyDirectory keys) : IEnvelopeVerifier
{
    private readonly IPublicKeyDirectory _keys = keys;

    public string Algorithm => SignatureFormat.Ed25519;

    public SignatureCheck Verify(string keyId, byte[] canonicalBytes, string signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return SignatureCheck.Refused(SignatureStatus.Missing, "envelope carries no sig");

        if (!SignatureFormat.TryDecode(signature, out var algorithm, out var raw))
            return SignatureCheck.Refused(SignatureStatus.MalformedFormat, signature);

        if (!string.Equals(algorithm, SignatureFormat.Ed25519, StringComparison.Ordinal))
            return SignatureCheck.Refused(SignatureStatus.UnsupportedAlgorithm, algorithm);

        if (!_keys.TryGetPublicKey(keyId, out var publicKey))
            return SignatureCheck.Refused(SignatureStatus.UnknownKey, keyId);

        return Ed25519KeyPair.VerifyRaw(publicKey, canonicalBytes, raw)
            ? SignatureCheck.Ok()
            : SignatureCheck.Refused(SignatureStatus.BadSignature, keyId);
    }
}
