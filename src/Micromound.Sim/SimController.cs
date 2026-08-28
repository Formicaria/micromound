using System.Text.Json;
using Micromound.Crypto;
using Micromound.Protocol;
using Micromound.Sync;

namespace Micromound.Sim;

/// <summary>
/// An in-process upstream controller — the other end of the wire, as a test double.
///
/// It does what UPSTREAM.md requires of any controller and nothing a specific product would add:
/// binds device keys at enrollment, signs every downlink envelope, verifies every uplink
/// signature AND the hash chain, stores what it received, acknowledges cumulatively, and never
/// dials the mound — the mound dials it, through <see cref="SimLink"/>.
///
/// It exists so end-to-end tests exercise both sides of every rule: a mound that signs correctly
/// proves little until something on the other end refuses the envelope that doesn't.
/// </summary>
public sealed class SimController
{
    private readonly Dictionary<string, MoundAccount> _mounds = new(StringComparer.Ordinal);
    private readonly InMemoryPublicKeyDirectory _deviceKeys = new();
    private readonly Ed25519EnvelopeVerifier _uplinkVerifier;
    private readonly Ed25519EnvelopeSigner _signer;
    private long _downlinkSeq;

    public SimController()
    {
        Keys = Ed25519KeyPair.Generate();
        _uplinkVerifier = new Ed25519EnvelopeVerifier(_deviceKeys);
        _signer = new Ed25519EnvelopeSigner(KeyIds.Controller, Keys);
    }

    public Ed25519KeyPair Keys { get; }

    public byte[] PublicKey => Keys.PublicKey;

    /// <summary>Uplink that was dropped, with reasons. Dropped and audited, never processed.</summary>
    public IReadOnlyList<string> Audit => _audit;

    private readonly List<string> _audit = [];

    /// <summary>
    /// Enrollment, compressed to its effect — PROTOCOL.md §3: the device's public key becomes
    /// known, and the controller's public key goes back. From here on, only signed traffic.
    ///
    /// Idempotent for a mound already known, because reconnection is NOT re-enrollment: a device
    /// that restarted keeps its account, and above all keeps its chain anchor — resetting that
    /// here would make every restart look like a fresh chain and turn the backlog a restarted
    /// mound faithfully preserved into a wall of chain refusals.
    /// </summary>
    public byte[] Enroll(string moundId, byte[] devicePublicKey)
    {
        // First enrollment binds the key; after that the binding is immutable here. There is no
        // self-service re-key — a rotation needs an operator-minted token (PROTOCOL.md §3), and a
        // controller that let any caller overwrite a known mound's key would hand the mesh to
        // whoever asked second.
        if (!_mounds.ContainsKey(moundId))
        {
            _deviceKeys.Register(moundId, devicePublicKey);
            _mounds[moundId] = new MoundAccount();
        }

        return PublicKey;
    }

    public void IssueCharter(Charter charter, DateTimeOffset now) =>
        QueueDownlink(charter.MoundId, EnvelopeKinds.Charter, charter, now);

    public void PushConfig(MoundManifest manifest, DateTimeOffset now) =>
        QueueDownlink(manifest.MoundId, EnvelopeKinds.Config, manifest, now);

    public void AssignMission(Mission mission, DateTimeOffset now) =>
        QueueDownlink(mission.MoundId, EnvelopeKinds.Mission, mission, now);

    public void OrderStop(string moundId, string reason, DateTimeOffset now) =>
        QueueDownlink(moundId, EnvelopeKinds.Stop, new { reason }, now);

    /// <summary>What this controller has verified and stored from one mound.</summary>
    public MoundAccount Account(string moundId) => _mounds[moundId];

    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// One uplink envelope arrives. Signature first, then the chain, then — and only then — the
    /// body. The response carries any pending downlink plus a cumulative ack.
    /// </summary>
    internal bool Exchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail)
    {
        detail = "";
        var response = new List<Envelope>();
        downlink = response;

        if (!_mounds.TryGetValue(uplink.MoundId, out var account))
        {
            // A key the directory does not hold is a refusal — there is no trust-on-first-use,
            // and an unenrolled mound gets silence rather than a conversation.
            _audit.Add($"uplink from unenrolled mound '{uplink.MoundId}' dropped");
            return true;
        }

        var check = EnvelopeValidator.Validate(uplink, _uplinkVerifier, uplink.MoundId);
        if (!check.IsValid)
        {
            // Dropped and audited, never processed — and never acknowledged: an ack would tell
            // the device to discard a record nobody accepted.
            account.Refusals++;
            _audit.Add($"uplink seq {uplink.Seq} from '{uplink.MoundId}' dropped: " +
                       string.Join("; ", check.Errors));
            return true;
        }

        // Re-delivery of something already acknowledged is normal — the ack may have been lost.
        // Idempotent: re-ack, process nothing.
        if (uplink.Seq <= account.AckedSeq)
        {
            response.Add(SignedAck(uplink, account.AckedSeq, [], "duplicate"));
            return true;
        }

        // The chain — PROTOCOL.md §6. An envelope that does not continue from the last verified
        // digest is a gap or a reordering, and processing it would make tampering unremarkable.
        if (uplink.Seq != account.AckedSeq + 1 ||
            !string.Equals(uplink.PrevDigest, account.AnchorDigest, StringComparison.Ordinal))
        {
            account.Refusals++;
            _audit.Add($"uplink seq {uplink.Seq} from '{uplink.MoundId}' breaks the chain " +
                       $"(expected seq {account.AckedSeq + 1} anchored at '{account.AnchorDigest}')");
            return true;
        }

        var evidenceIds = Store(uplink, account);

        account.AckedSeq = uplink.Seq;
        account.AnchorDigest = uplink.Digest();

        // Pending downlink drains with the response, once.
        response.AddRange(account.Downlink);
        account.Downlink.Clear();

        response.Add(SignedAck(uplink, account.AckedSeq, evidenceIds, ""));
        return true;
    }

    private List<string> Store(Envelope uplink, MoundAccount account)
    {
        var evidenceIds = new List<string>();

        switch (uplink.Kind)
        {
            case EnvelopeKinds.ActionRecord:
                if (Body<ActionRecord>(uplink) is { } record) account.Records.Add(record);
                break;

            case EnvelopeKinds.EvidenceBundle:
                if (Body<EvidenceBundle>(uplink) is { } bundle)
                    foreach (var item in bundle.Items)
                    {
                        account.Evidence[item.EvidenceId] = item;
                        evidenceIds.Add(item.EvidenceId);
                    }
                break;

            case EnvelopeKinds.MissionReport:
                if (Body<MissionReport>(uplink) is { } report) account.Reports.Add(report);
                break;

            case EnvelopeKinds.MoundSync:
                account.LastSync = uplink;
                break;

            case EnvelopeKinds.Ack:
                if (Body<AckBody>(uplink) is { } ack) account.MoundAcks.Add(ack);
                break;
        }

        return evidenceIds;
    }

    /// <summary>
    /// Queue any signed downlink envelope. Public because tests need to send a mound things a
    /// well-behaved controller never would — an unknown kind, an uplink-only kind — and prove
    /// the mound refuses them loudly rather than trusting the sender's good manners.
    /// </summary>
    public void QueueDownlink<T>(string moundId, string kind, T body, DateTimeOffset now)
    {
        var envelope = new Envelope
        {
            MoundId = moundId,
            Seq = _downlinkSeq++,
            SentAt = now.ToWire(),
            Kind = kind,
            Body = JsonSerializer.SerializeToElement(body, ProtocolJson.Options),
            PrevDigest = ""   // downlink is signature-verified; only the uplink stream is chained
        };

        EnvelopeSigning.Sign(envelope, _signer);
        _mounds[moundId].Downlink.Add(envelope);
    }

    private Envelope SignedAck(Envelope uplink, long throughSeq, List<string> evidenceIds, string detail)
    {
        var envelope = new Envelope
        {
            MoundId = uplink.MoundId,
            Seq = _downlinkSeq++,
            SentAt = uplink.SentAt,   // the sim has no clock of its own; echo the beat it answers
            Kind = EnvelopeKinds.Ack,
            Body = JsonSerializer.SerializeToElement(new AckBody
            {
                RefersTo = uplink.Id,
                ThroughSeq = throughSeq,
                EvidenceIds = evidenceIds,
                Detail = detail
            }, ProtocolJson.Options),
            PrevDigest = ""
        };

        EnvelopeSigning.Sign(envelope, _signer);
        return envelope;
    }

    private static T? Body<T>(Envelope envelope) where T : class
    {
        try
        {
            return envelope.Body.Deserialize<T>(ProtocolJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Everything the controller holds about one enrolled mound.</summary>
    public sealed class MoundAccount
    {
        public long AckedSeq { get; internal set; } = -1;
        public string AnchorDigest { get; internal set; } = "";
        public int Refusals { get; internal set; }
        public List<Envelope> Downlink { get; } = [];
        public List<ActionRecord> Records { get; } = [];
        public Dictionary<string, EvidenceItem> Evidence { get; } = new(StringComparer.Ordinal);
        public List<MissionReport> Reports { get; } = [];
        public List<AckBody> MoundAcks { get; } = [];
        public Envelope? LastSync { get; internal set; }
    }
}

/// <summary>
/// The wire between a mound and the controller, with a switch. Offline is a normal state, not an
/// error — flipping <see cref="Online"/> off is how every disconnection scenario in the test
/// suite happens, and nothing else in either endpoint knows the difference.
/// </summary>
public sealed class SimLink(SimController controller) : ISyncTransport
{
    public bool Online { get; set; } = true;

    /// <summary>Envelopes that crossed while online — the wire's own count, for tests.</summary>
    public int Exchanges { get; private set; }

    public bool TryExchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail)
    {
        if (!Online)
        {
            downlink = [];
            detail = "link offline";
            return false;
        }

        Exchanges++;
        return controller.Exchange(uplink, out downlink, out detail);
    }
}
