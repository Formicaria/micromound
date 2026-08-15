using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using BcEd25519Signer = Org.BouncyCastle.Crypto.Signers.Ed25519Signer;

namespace Micromound.Crypto;

/// <summary>
/// A device or colony Ed25519 identity — PROTOCOL.md §3. The private seed is generated on-device
/// and never leaves it; SAFETY.md prohibits any endpoint or envelope that reads one back, so
/// nothing here serializes <see cref="Seed"/> and callers should not either.
/// </summary>
public sealed class Ed25519KeyPair
{
    public const int SeedLength = 32;
    public const int PublicKeyLength = 32;
    public const int SignatureLength = 64;

    private readonly Ed25519PrivateKeyParameters _privateKey;
    private readonly byte[] _seed;

    private Ed25519KeyPair(byte[] seed)
    {
        _seed = seed;
        _privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        PublicKey = _privateKey.GeneratePublicKey().GetEncoded();
    }

    /// <summary>The 32-byte public key, safe to publish and to bind to a mound record.</summary>
    public byte[] PublicKey { get; }

    /// <summary>
    /// The 32-byte private seed. Present so a runtime can persist its own identity to protected
    /// local storage; never put this on the wire.
    /// </summary>
    public byte[] Seed => (byte[])_seed.Clone();

    public static Ed25519KeyPair Generate() => new(RandomNumberGenerator.GetBytes(SeedLength));

    /// <summary>Rehydrate a stored identity, or build a deterministic one for tests.</summary>
    public static Ed25519KeyPair FromSeed(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Length != SeedLength)
            throw new ArgumentException(
                $"An Ed25519 seed is {SeedLength} bytes; got {seed.Length}.", nameof(seed));

        return new Ed25519KeyPair((byte[])seed.Clone());
    }

    public byte[] SignRaw(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var signer = new BcEd25519Signer();
        signer.Init(true, _privateKey);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    /// <summary>
    /// Verification never throws on malformed input — a bad key or a truncated signature is a
    /// refusal to report, not an exception to handle at every call site.
    /// </summary>
    public static bool VerifyRaw(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey is null || publicKey.Length != PublicKeyLength) return false;
        if (signature is null || signature.Length != SignatureLength) return false;
        if (message is null) return false;

        try
        {
            var verifier = new BcEd25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
