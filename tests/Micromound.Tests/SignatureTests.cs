using Micromound.Crypto;
using Micromound.Protocol;
using Micromound.Sim;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// PROTOCOL.md §2: "Unsigned or badly signed envelopes are dropped and audited, never processed."
/// SAFETY.md: "Prohibited by construction — unsigned protocol traffic." These prove there is no
/// unsigned path, not even a convenient one for tests.
/// </summary>
public class SignatureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    private static Envelope Beat(string moundId = "mm-1", long seq = 0) => new()
    {
        Id = "11111111-1111-4111-8111-111111111111",
        MoundId = moundId,
        Seq = seq,
        SentAt = Now.ToWire(),
        Kind = EnvelopeKinds.MoundSync,
        Body = System.Text.Json.JsonSerializer.SerializeToElement(new { beat = seq }, ProtocolJson.Options)
    };

    private static (Ed25519EnvelopeSigner Signer, Ed25519EnvelopeVerifier Verifier) Pair(string keyId)
    {
        var keys = Ed25519KeyPair.Generate();
        var directory = new InMemoryPublicKeyDirectory();
        directory.Register(keyId, keys.PublicKey);
        return (new Ed25519EnvelopeSigner(keyId, keys), new Ed25519EnvelopeVerifier(directory));
    }

    [Fact]
    public void A_signed_envelope_verifies()
    {
        var (signer, verifier) = Pair("mm-1");
        var envelope = EnvelopeSigning.Sign(Beat(), signer);

        Assert.StartsWith("ed25519:", envelope.Signature);
        Assert.True(EnvelopeSigning.Verify(envelope, verifier, "mm-1").IsValid);
    }

    [Fact]
    public void An_unsigned_envelope_is_refused()
    {
        var (_, verifier) = Pair("mm-1");
        var check = EnvelopeSigning.Verify(Beat(), verifier, "mm-1");

        Assert.False(check.IsValid);
        Assert.Equal(SignatureStatus.Missing, check.Status);
    }

    [Fact]
    public void Tampering_with_any_covered_field_invalidates_the_signature()
    {
        var (signer, verifier) = Pair("mm-1");
        var envelope = EnvelopeSigning.Sign(Beat(), signer);

        envelope.Seq = 99; // seq is covered by the canonical bytes

        var check = EnvelopeSigning.Verify(envelope, verifier, "mm-1");
        Assert.False(check.IsValid);
        Assert.Equal(SignatureStatus.BadSignature, check.Status);
    }

    [Fact]
    public void A_key_the_colony_never_enrolled_is_refused()
    {
        var (signer, _) = Pair("mm-1");
        var envelope = EnvelopeSigning.Sign(Beat(), signer);

        var empty = new Ed25519EnvelopeVerifier(new InMemoryPublicKeyDirectory());
        var check = EnvelopeSigning.Verify(envelope, empty, "mm-1");

        Assert.False(check.IsValid);
        Assert.Equal(SignatureStatus.UnknownKey, check.Status);
    }

    [Fact]
    public void Another_mounds_signature_does_not_pass_as_this_mounds()
    {
        var impostorKeys = Ed25519KeyPair.Generate();
        var honestKeys = Ed25519KeyPair.Generate();

        var directory = new InMemoryPublicKeyDirectory();
        directory.Register("mm-1", honestKeys.PublicKey);
        var verifier = new Ed25519EnvelopeVerifier(directory);

        var envelope = EnvelopeSigning.Sign(Beat(), new Ed25519EnvelopeSigner("mm-1", impostorKeys));

        Assert.Equal(SignatureStatus.BadSignature, EnvelopeSigning.Verify(envelope, verifier, "mm-1").Status);
    }

    [Theory]
    [InlineData("", SignatureStatus.Missing)]
    [InlineData("   ", SignatureStatus.Missing)]
    [InlineData("ed25519:", SignatureStatus.MalformedFormat)]
    [InlineData("ed25519:zzzz", SignatureStatus.MalformedFormat)]
    [InlineData("ed25519:abc", SignatureStatus.MalformedFormat)]   // odd-length hex
    [InlineData("deadbeef", SignatureStatus.MalformedFormat)]      // no algorithm separator
    [InlineData("rsa:00ff", SignatureStatus.UnsupportedAlgorithm)]
    [InlineData("ed25519:00ff", SignatureStatus.BadSignature)]     // right shape, wrong length
    public void Malformed_signatures_are_refused_with_a_specific_reason(string signature, SignatureStatus expected)
    {
        var (_, verifier) = Pair("mm-1");
        var envelope = Beat();
        envelope.Signature = signature;

        var check = EnvelopeSigning.Verify(envelope, verifier, "mm-1");
        Assert.False(check.IsValid);
        Assert.Equal(expected, check.Status);
    }

    [Fact]
    public void Revoking_a_key_stops_that_mound_being_believed()
    {
        var keys = Ed25519KeyPair.Generate();
        var directory = new InMemoryPublicKeyDirectory();
        directory.Register("mm-1", keys.PublicKey);
        var verifier = new Ed25519EnvelopeVerifier(directory);
        var envelope = EnvelopeSigning.Sign(Beat(), new Ed25519EnvelopeSigner("mm-1", keys));

        Assert.True(EnvelopeSigning.Verify(envelope, verifier, "mm-1").IsValid);

        Assert.True(directory.Revoke("mm-1"));
        Assert.Equal(SignatureStatus.UnknownKey, EnvelopeSigning.Verify(envelope, verifier, "mm-1").Status);
    }

    [Fact]
    public void Envelope_validation_reports_signature_failure_alongside_every_other_reason()
    {
        var (_, verifier) = Pair("mm-1");
        var envelope = Beat();
        envelope.Kind = "mystery_kind";

        var result = EnvelopeValidator.Validate(envelope, verifier, "mm-1");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("refused_unknown_kind"));
        Assert.Contains(result.Errors, e => e.Contains("signature_refused"));
    }

    [Fact]
    public void The_same_seed_always_yields_the_same_identity()
    {
        var seed = new byte[Ed25519KeyPair.SeedLength];
        for (var i = 0; i < seed.Length; i++) seed[i] = (byte)i;

        Assert.Equal(Ed25519KeyPair.FromSeed(seed).PublicKey, Ed25519KeyPair.FromSeed(seed).PublicKey);
        Assert.NotEqual(Ed25519KeyPair.FromSeed(seed).PublicKey, Ed25519KeyPair.Generate().PublicKey);
    }

    [Fact]
    public void A_seed_of_the_wrong_length_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Ed25519KeyPair.FromSeed(new byte[16]));
    }
}

/// <summary>
/// The colony's view of a reconnecting mound: a backlog is only believed if the chain holds
/// AND every envelope in it is signed by the key that mound enrolled with.
/// </summary>
public class SignedBacklogTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    [Fact]
    public void A_drained_backlog_verifies_chain_and_signatures_together()
    {
        var mound = new SimMound("mm-1");
        var directory = new InMemoryPublicKeyDirectory();
        directory.Register(mound.MoundId, mound.PublicKey);

        for (var i = 0; i < 4; i++)
            mound.EnqueueUplink(EnvelopeKinds.MoundSync, new { beat = i }, Now.AddSeconds(i));

        var backlog = mound.DrainUplink();
        var result = EnvelopeValidator.ValidateChain(
            backlog, "", new Ed25519EnvelopeVerifier(directory), mound.MoundId);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void One_forged_envelope_spoils_an_otherwise_intact_backlog()
    {
        var mound = new SimMound("mm-1");
        var directory = new InMemoryPublicKeyDirectory();
        directory.Register(mound.MoundId, mound.PublicKey);

        for (var i = 0; i < 3; i++)
            mound.EnqueueUplink(EnvelopeKinds.MoundSync, new { beat = i }, Now.AddSeconds(i));

        var backlog = mound.DrainUplink().ToList();
        backlog[1].Signature = "ed25519:" + new string('0', 128); // structurally fine, cryptographically not

        var result = EnvelopeValidator.ValidateChain(
            backlog, "", new Ed25519EnvelopeVerifier(directory), mound.MoundId);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("seq 1") && e.Contains("signature_refused"));
    }

    [Fact]
    public void Signing_does_not_disturb_the_hash_chain()
    {
        var mound = new SimMound("mm-1");
        for (var i = 0; i < 3; i++)
            mound.EnqueueUplink(EnvelopeKinds.MoundSync, new { beat = i }, Now.AddSeconds(i));

        var backlog = mound.DrainUplink();

        // Chain validation alone still passes: `sig` is excluded from the canonical bytes.
        Assert.True(EnvelopeValidator.ValidateChain(backlog, "").IsValid);
        Assert.All(backlog, e => Assert.StartsWith("ed25519:", e.Signature));
    }
}
