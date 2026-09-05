using System.Text;
using System.Text.Json;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// Freezes the exact bytes that go on the wire and the digests that chain them.
///
/// Everything here is fixed: no clocks, no GUIDs, no keys. Signatures are deliberately absent
/// because `sig` is excluded from the canonical bytes — that is precisely the property the C
/// mirror needs, since it lets a device sign and chain without re-serializing.
/// </summary>
public class CanonicalBytesTests
{
    private static readonly DateTimeOffset Fixed = DateTimeOffset.Parse("2026-08-14T21:04:11Z");

    private static Envelope Envelope(string id, long seq, string kind, object body, string prevDigest) => new()
    {
        Id = id,
        MoundId = "mm-7f3a0000-0000-4000-8000-000000000001",
        Seq = seq,
        SentAt = Fixed.AddSeconds(seq).ToWire(),
        Kind = kind,
        Body = JsonSerializer.SerializeToElement(body, ProtocolJson.Options),
        PrevDigest = prevDigest,
        Signature = "ed25519:not-covered-by-canonical-bytes"
    };

    private static Charter GoldenCharter() => new()
    {
        CharterId = "c0000000-0000-4000-8000-000000000001",
        MoundId = "mm-7f3a0000-0000-4000-8000-000000000001",
        MissionRef = "mission-0001",
        IssuedAt = Fixed.ToWire(),
        ExpiresAt = Fixed.AddHours(1).ToWire(),
        LeaseTtlSeconds = 900,
        ActionCeiling = "benign",
        Capabilities = ["sense.temp", "act.relay_1"],
        Limits = { ["act.relay_1"] = new CapabilityLimits { MaxOnSeconds = 30, MinOffSeconds = 300 } },
        Evidence = new EvidencePolicy { RequiredFor = ["act.*"], MinIntervalSeconds = 60 },
        SafeState = "all_actuators_off",
        SyncIntervalSeconds = 15
    };

    private static ActionRecord GoldenActionRecord() => new()
    {
        ActionId = "a0000000-0000-4000-8000-000000000001",
        CharterId = "c0000000-0000-4000-8000-000000000001",
        Capability = "act.relay_1",
        Parameters = { ["on_s"] = 30 },
        StartedAt = Fixed.ToWire(),
        EndedAt = Fixed.AddSeconds(30).ToWire(),
        Outcome = ActionOutcomes.Succeeded,
        EvidenceRefs = ["e0000000-0000-4000-8000-000000000001"],
        Detail = ""
    };

    private static EvidenceBundle GoldenEvidenceBundle() => new()
    {
        BundleId = "b0000000-0000-4000-8000-000000000001",
        Items =
        [
            new EvidenceItem
            {
                EvidenceId = "e0000000-0000-4000-8000-000000000001",
                Type = "sensor_window",
                CapturedAt = Fixed.AddSeconds(30).ToWire(),
                Source = "sim.act.relay_1",
                PayloadJson = """{"before":0,"after":1}""",
                ContentDigest = ""
            }
        ]
    };

    // The two bodies a Pi-class mound and a full controller both encode, and which no golden
    // pinned before M3. A constrained controller never decodes a mission (§8 keeps it out of the
    // reduced profile), which is why the omission was reasonable — but a Pi and a controller do,
    // and nothing checked that they agree. These freeze the field order, naming, and default
    // emission of both, exactly as the charter/record/bundle bodies above are frozen.
    private static Mission GoldenMission() => new()
    {
        MissionId = "d0000000-0000-4000-8000-000000000001",
        MoundId = "mm-7f3a0000-0000-4000-8000-000000000001",
        CharterId = "c0000000-0000-4000-8000-000000000001",
        RequiredCapabilities = ["sense.temp"],
        Steps =
        [
            new MissionStep { StepId = "read_before", Op = MissionStepOps.Sense,
                Capability = "sense.temp", EvidenceTag = "temp_before" },
            new MissionStep { StepId = "cool", Op = MissionStepOps.Act, Capability = "act.relay_1",
                Parameters = { ["on_s"] = 30 },
                Condition = new StepCondition { SourceStep = "read_before", Op = ConditionOps.GreaterThan, Value = 28 },
                EvidenceTag = "cooling_action" },
            new MissionStep { StepId = "read_after", Op = MissionStepOps.Verify,
                Capability = "sense.temp", Confirms = "cool", EvidenceTag = "temp_after" }
        ],
        RequiredEvidence = ["temp_before", "cooling_action", "temp_after"],
        SafeState = "all_actuators_off",
        ExpiresAt = Fixed.AddMinutes(30).ToWire(),
        Context = "hold the enclosure under 28C"
    };

    private static MissionReport GoldenMissionReport() => new()
    {
        MissionId = "d0000000-0000-4000-8000-000000000001",
        CharterId = "c0000000-0000-4000-8000-000000000001",
        State = MissionStates.Completed,
        StartedAt = Fixed.ToWire(),
        EndedAt = Fixed.AddSeconds(30).ToWire(),
        Steps =
        [
            new MissionStepResult { StepId = "read_before", State = MissionStepStates.Executed,
                Value = 31, EvidenceRefs = ["e0000000-0000-4000-8000-000000000001"] },
            new MissionStepResult { StepId = "cool", State = MissionStepStates.Executed,
                ActionId = "a0000000-0000-4000-8000-000000000001",
                EvidenceRefs = ["e0000000-0000-4000-8000-000000000002"] },
            new MissionStepResult { StepId = "read_after", State = MissionStepStates.Executed,
                Value = 26, EvidenceRefs = ["e0000000-0000-4000-8000-000000000003"] }
        ]
    };

    [Fact]
    public void Canonical_bytes_and_digests_are_frozen()
    {
        var report = new StringBuilder();
        report.AppendLine("# MICROMOUND canonical wire bytes — golden fixture");
        report.AppendLine("#");
        report.AppendLine("# Frozen by tests/Micromound.Tests/Golden/CanonicalBytesTests.cs.");
        report.AppendLine("# The M3 ESP32 C mirror must produce byte-identical output for the same input.");
        report.AppendLine("# `sig` is excluded from canonical bytes by construction — see Envelope.CanonicalBytes.");
        report.AppendLine();

        // A short chain, so the fixture pins prev_digest linkage and not just single envelopes.
        var previous = Append(report, Envelope("11111111-1111-4111-8111-111111111111", 0,
            EnvelopeKinds.MoundSync, new { state = "chartered", uptime_s = 3600 }, ""));

        previous = Append(report, Envelope("22222222-2222-4222-8222-222222222222", 1,
            EnvelopeKinds.ActionRecord, GoldenActionRecord(), previous));

        previous = Append(report, Envelope("33333333-3333-4333-8333-333333333333", 2,
            EnvelopeKinds.EvidenceBundle, GoldenEvidenceBundle(), previous));

        previous = Append(report, Envelope("44444444-4444-4444-8444-444444444444", 3,
            EnvelopeKinds.Charter, GoldenCharter(), previous));

        previous = Append(report, Envelope("55555555-5555-4555-8555-555555555555", 4,
            EnvelopeKinds.MissionReport, GoldenMissionReport(), previous));

        Append(report, Envelope("66666666-6666-4666-8666-666666666666", 5,
            EnvelopeKinds.Mission, GoldenMission(), previous));

        GoldenFile.Verify("canonical-envelopes.txt", report.ToString());
    }

    [Fact]
    public void Bare_contract_serialization_is_frozen()
    {
        var report = new StringBuilder();
        report.AppendLine("# MICROMOUND contract bodies — golden fixture");
        report.AppendLine("#");
        report.AppendLine("# Field order, naming, and default emission for every typed body.");
        report.AppendLine();

        AppendBody(report, "charter", GoldenCharter());
        AppendBody(report, "action_record", GoldenActionRecord());
        AppendBody(report, "evidence_bundle", GoldenEvidenceBundle());
        AppendBody(report, "mission", GoldenMission());
        AppendBody(report, "mission_report", GoldenMissionReport());

        GoldenFile.Verify("canonical-bodies.txt", report.ToString());
        return;

        static void AppendBody(StringBuilder into, string label, object body)
        {
            into.AppendLine($"## {label}");
            into.AppendLine(JsonSerializer.Serialize(body, ProtocolJson.Options));
            into.AppendLine();
        }
    }

    [Fact]
    public void A_round_trip_through_json_preserves_the_digest()
    {
        var envelope = Envelope("11111111-1111-4111-8111-111111111111", 7, EnvelopeKinds.ActionRecord,
            GoldenActionRecord(), "sha256:00");

        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        var restored = JsonSerializer.Deserialize<Envelope>(json, ProtocolJson.Options);

        Assert.NotNull(restored);
        Assert.Equal(envelope.Digest(), restored.Digest());
    }

    [Fact]
    public void A_mission_report_round_trips_through_json_preserving_the_digest()
    {
        // mission_report is uplink, so it chains: its digest is what the controller verifies. A
        // decode-and-re-encode that shifted a byte would break the chain at exactly this envelope.
        var envelope = Envelope("55555555-5555-4555-8555-555555555555", 4, EnvelopeKinds.MissionReport,
            GoldenMissionReport(), "sha256:00");

        var json = JsonSerializer.Serialize(envelope, ProtocolJson.Options);
        var restored = JsonSerializer.Deserialize<Envelope>(json, ProtocolJson.Options);

        Assert.NotNull(restored);
        Assert.Equal(envelope.Digest(), restored.Digest());
    }

    [Fact]
    public void The_mission_and_report_survive_a_decode_and_re_encode_unchanged()
    {
        // The cross-implementation contract stated directly: a body decoded and re-encoded is
        // byte-for-byte what it was. This is the property the M5 C mirror must hold for the two
        // bodies §8 keeps out of the reduced profile — the ones no other test exercised on the
        // wire until now.
        static void RoundTrips<T>(T body)
        {
            var first = JsonSerializer.Serialize(body, ProtocolJson.Options);
            var again = JsonSerializer.Serialize(
                JsonSerializer.Deserialize<T>(first, ProtocolJson.Options), ProtocolJson.Options);
            Assert.Equal(first, again);
        }

        RoundTrips(GoldenMission());
        RoundTrips(GoldenMissionReport());
    }

    [Fact]
    public void The_signature_is_outside_the_digest()
    {
        var envelope = Envelope("11111111-1111-4111-8111-111111111111", 7, EnvelopeKinds.MoundSync,
            new { beat = 1 }, "");
        var before = envelope.Digest();

        envelope.Signature = "ed25519:" + new string('a', 128);

        Assert.Equal(before, envelope.Digest());
    }

    [Fact]
    public void String_escaping_is_frozen()
    {
        // PROTOCOL.md §2: printable ASCII literal; \" and \\; \b \t \n \f \r; every other code point
        // below U+0020 and every code point from U+007F up as \uXXXX with UPPERCASE hex, surrogate
        // pairs above the BMP. No Unicode table anywhere — the property the C mirror depends on, and
        // the one the runtime's relaxed encoder never had.
        var report = new StringBuilder();
        report.AppendLine("# MICROMOUND canonical string escaping — golden fixture");
        report.AppendLine("#");
        report.AppendLine("# Frozen by tests/Micromound.Tests/Golden/CanonicalBytesTests.cs (CanonicalJsonEncoder).");
        report.AppendLine("# Format: <lowercase hex of the string's UTF-8 bytes> TAB <canonical JSON string literal>.");
        report.AppendLine("# An empty first column is the empty string.");
        report.AppendLine();

        string[] vectors =
        [
            "",
            "plain",
            "a\"b",
            "back\\slash",
            "slash/ok",
            "+<>&'",
            "=?`~!@#$%^*()[]{}|;:,.",
            "tab\tnl\ncr\rff\fbs\b",
            "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F",
            "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F",
            " ~",                                   // the two ends of the literal range
            "\u007F",                               // DEL: first escaped code point above ASCII printables
            "\u0080\u00A0\u00FF",                    // Latin-1 edge, NBSP
            "unicode \u00E9 \u00FC \u6F22\u5B57 \U0001F600",
            "mixed \"q\" and \\ and \u2028 line sep",
            "\u00A0\u2003\u200B\uFEFF",              // spaces and format characters a relaxed encoder treated unevenly
            "\uFFFD\uFFFE\uFFFF",                    // replacement and the BMP noncharacters
            "\U00010000\U0001F331\U0010FFFF",        // astral: first, an emoji, last
            "emoji \U0001F600 end",
            "{\"before\":0,\"after\":1}",
            "mound \"7f3a\" \\ enclosure\u00B0C",
            "\u0414\u0435\u0432\u0430\u0439\u0441 \u0627\u0644\u062C\u0647\u0627\u0632 \u30C7\u30D0\u30A4\u30B9",
        ];

        foreach (var s in vectors)
            report.AppendLine($"{Convert.ToHexStringLower(Encoding.UTF8.GetBytes(s))}\t{JsonSerializer.Serialize(s, ProtocolJson.Options)}");

        // Escaping applies to property names by the same rule.
        report.AppendLine();
        report.AppendLine("## as a property name and value");
        report.AppendLine(JsonSerializer.Serialize(new Dictionary<string, string> { ["k\u00E9y\"q"] = "v\\al\u2028" }, ProtocolJson.Options));

        GoldenFile.Verify("canonical-strings.txt", report.ToString());
    }

    [Fact]
    public void Double_formatting_is_frozen()
    {
        // Every number on the wire is a double, written the way the runtime writes it: the shortest
        // digit string that round-trips, laid out plain while the decimal point sits within
        // -3 <= digPos <= max(digits, 17) of the first significant digit (digPos = exponent + 1),
        // otherwise "d.dddE+XX" / "d.dddE-XX" with at least two exponent digits: 1e16 is plain,
        // 1e17 is "1E+17", 0.0001 is plain, 0.00001 is "1E-05". The bit patterns are fixed here
        // (not computed) so the fixture cannot drift with the arithmetic that produced them; the
        // text column is what the C formatter must reproduce. The first row after the named
        // values walks the plain/scientific boundary on both sides.
        const string bits = """
        4376345785d8a000 43abc16d674ec800 43e158e460913d00 4415af1d78b58c40 437b69b4ba630f35 4380a741a4627800 3f202e4b6ce5dc68 3ee9e3abe16fc70d
        0000000000000000 8000000000000000 3ff0000000000000 bff0000000000000 403e000000000000 3fe0000000000000 3fb999999999999a 3fc999999999999a
        3fd3333333333333 3fd3333333333334 3fd5555555555555 3fe5555555555555 403c000000000000 403f000000000000 403a000000000000 408c200000000000
        40ac200000000000 402e000000000000 404e000000000000 4072c00000000000 4010624dd2f1a9fc 4000624dd2f1a9fc 40189374bc6a7efa 3fd0624dd2f1a9fc
        3fe0624dd2f1a9fc 3ff0624dd2f1a9fc 419d6f3454000000 4271f71fb04cb000 42dc12218377de40 43118b54f22aeb00 4345ee2a2eb5a5c4 42d6bcc41e900000
        430c6bf526340000 4341c37937e08000 444b1ae4d6e2ef50 4480f0cf064dd592 54b249ad2594c37d 7e37e43c8800759c 7fefffffffffffff 3f50624dd2f1a9fc
        3f1a36e2eb1c432d 3ee4f8b588e368f1 3eb0c6f7a0b5ed8d 3e7ad7f29abcaf48 3ddb7cdfd9d7bdbb 2b2bff2ee48e0530 0000000000000001 0010000000000000
        405edd2f1a9fbe77 4058ff5c28f5c28f 4059000000000000 412e848000000000 3f202e7ef70994dd 40c81cd6e631f8a1 400921fb54442d18 4005bf0a8b145769
        3ff8000000000000 4004000000000000 3ee4f8b588e368f1 3ee9cb8320b15070 430c6bf52633ffff 430c6bf52633ffff 3fd3333333333334 4011666666666666
        4020666666666666 3ff199999999999a 400199999999999a 400a666666666666 4059200000000000 c071126666666666 44dfe185ca57c517 3c07a4da290c1653
        bf50624dd2f1a9fc c44b1ae4d6e2ef50 8000000000000001 632b808fafc222ca 69e53e6142b05b40 214ec5e398654f88 318dd1d799b03131 1142382a9ee6e950
        718a818f8bf7b4e4 0d461adef4bb8e22 5a8f8adebaf0f258 5dc274f6e212933f 1f64932b85b7eea7 423ea9af32d5f689 62a8e205becf8852 3956abdf0d401520
        39c27db14f6f9115 514f9243aaa8a373 6d24a2f4ce096ed3 527a36d14e859f0d 3f93c3883c13b4e8 5b0f4c4c1c80f69b 16a60f72002226fd 2b812b58384308b2
        02866e8ea9c07a8b 624e05d83f7bd3c9 5b07ede7f10ba656 52da52ff434e0cf4 09b10213b954e0e8 202142491b024ffc 45a37493e69c2773 626f689068733a98
        5b9a23a46da0aaba 46d0d77b88a1e665 2591732218a8ac29 3b500a0a39e39053 51c8eb66c293b24a 0e95d67e2dc2b5a8 088f8969fcdddc93 238633056cdb2e5c
        528e57f5c0345792 6c4519b84fbda132 5fe4134ffcd9776e 233d1eca43c805c0 66c6826ffc41acf6 7dd2a95832e50dc5 0097f454250b5237 6e258ba76cb3983c
        615b8e5c72b39615 7266bd57814dc366 09f0095f179677f8 785c8363b4ae04fb 169c92c2bd9d10a0 6ba3d286394e5316 49c7eaeb2276dafe 40e318422c6ba2f3
        011c9e33b349f3d4 6b147bf70902af38 44e5382fd568bb8f 35bdca82c7591a32 5f87baed01e2ba63 56b2c463721b5f57 70c47ce9fec65343 695751ff497b9a7f
        1cdebf3fc859cd86 0bc4a72f4fa53d09 61a461fcc8491766 723cd8f3e4a81e1d 5680e4f22ffc5287 333df1525b07b6e4 03ff60fd9b6cddd3 799372e62cb837b0
        687b06dee9c30706 3582d953ec3cc549 43469b96551407a7 4b740a386c85f151 100858dfa1b38d70 085fec00b2ce4825 1f91d6a71195065a 771427c3341b52b6
        397a3fa717800c77 101ae3af2b0f61a1 4599b865757955fc 0506588d80c8d2d1 0a8dc3148728d267 55d8fd059d4b0862 2b9c35984d4bbd90 072b950a5d22c336
        4fce8fa56b88cd66 43f1b0cc557ee7dd 71bb59ed13f68068 1acd4f2e34bc59b8 7e72a129817f0227 1d2f7326d54ccaef 05220aaea7a17694 0ec2b95d6f42c07f
        1a17427f7e1f748b 22ae6be549726e36 29cba7525032ddfb 409bf1a50eec93a9 7991fe9b87d01316 6a06cc949a006a02 3e652b1e2bc4d2c0 3dc1bb018ebbf01d
        271787bca855d996 25bb505328839b30 647fc18339a08262 64cd77dcd49042f3 607ca1048c8a769b 7dbe425eef53c33c 78472c9b9f9c4068 48b4d72df64f75aa
        61a0d8432468e75d 63f4377b6fc6c6cf 77def0e11b541764 428a172528171561 6ff27bd0b2bee5ba 021d530451c10414 6beb37f40897070c 3900dd7cd9a53122
        0bf3cc43e0e26289 665ef7ebed5029b8 08eb2198fb115ea5 40870b163ed0f627 401f47fcb923a29c 4089213333333333 401108fc9a19f21a 408b474b60f1b25f
        4082eccccccccccd 406d3ee5f30e7ff6 4089980000000000 408a98cccccccccd 408cd9369ec2ce46 408c3a488a47ecff 405cff5c28f5c28f 4073d66666666666
        4079220c1fc8f323 405ec00000000000 4061166666666666 4085ab8f5c28f5c3 406c8508461f9f02 408b1622d0e56042 4081e7b2e514c22f 4086580000000000
        408e51999999999a 4054133333333333 408a448112acaaee 40711ed0e5604189 406162b367a0f909 40835d374bc6a7f0 4064c00000000000 408673a69595feda
        406ad59c23b7952d 408a9d5c28f5c28f 4087ac7102363b25 40865278d4fdf3b6 4080e5103b81b64e 407a7e6666666666 406e20a3d70a3d71 40838f3333333333
        404813510d38cda7 40879599103c8e26 4070a00000000000 40728ad6a161e4f7 4079d96685db76b4 408ea50ebedfa440 4081773333333333 4071bccccccccccd
        408ce00000000000 4071d03b302206bb 408c785604189375 407644cccccccccd 4082a748d3ae685e 4084f80000000000 407c900000000000 406389757b41bfbe
        408e77d2f1a9fbe7 407840b439581062 40701c86114170e4 4078b00000000000 407723ae147ae148 408c380000000000 408824cccccccccd 405268624dd2f1aa
        c045a978d4fdf3b6 3f747ae147ae147b c04d2df3b645a1cb c048b33333333333 c0573cdd2f1a9fbe c055adf3b645a1cb 4045fc6a7ef9db23 c0490f9db22d0e56
        403de872b020c49c c02abc6a7ef9db23 401e26e978d4fdf4 c04d36c8b4395810 4036a395810624dd c029cd4fdf3b645a c04a022d0e560419 c055cf5c28f5c28f
        c04255810624dd2f 402ba978d4fdf3b6 c04eb3f7ced91687 402856872b020c4a c05404083126e979 405275d2f1a9fbe7 4054575c28f5c28f 4056e4fdf3b645a2
        c0587c083126e979 404f1fdf3b645a1d c0502d4fdf3b645a 401ca8f5c28f5c29 403013f7ced91687 bfed26e978d4fdf4 c04194fdf3b645a2 c02638d4fdf3b646
        c03050a3d70a3d71 404c83126e978d50 c0368d4fdf3b645a 4026b126e978d4fe c04919374bc6a7f0 4051322d0e560419 c052f29fbe76c8b4
        """;

        var report = new StringBuilder();
        report.AppendLine("# MICROMOUND canonical double formatting — golden fixture");
        report.AppendLine("#");
        report.AppendLine("# Frozen by tests/Micromound.Tests/Golden/CanonicalBytesTests.cs.");
        report.AppendLine("# Format: <hex IEEE-754 bits, big-endian> TAB <canonical JSON number>.");
        report.AppendLine();

        foreach (var hex in bits.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var value = BitConverter.Int64BitsToDouble(Convert.ToInt64(hex, 16));
            Assert.True(double.IsFinite(value), $"fixture bit pattern {hex} is not a finite double");
            report.AppendLine($"{hex}\t{JsonSerializer.Serialize(value, ProtocolJson.Options)}");
        }

        // The same formatter inside a body, where the C mirror meets it.
        report.AppendLine();
        report.AppendLine("## in a body");
        report.AppendLine(JsonSerializer.Serialize(new Dictionary<string, double>
        {
            ["on_s"] = 30, ["temp_c"] = 28.4, ["ratio"] = 1.0 / 3, ["tiny"] = 1e-7, ["huge"] = 1e21, ["neg_zero"] = -0.0
        }, ProtocolJson.Options));

        GoldenFile.Verify("canonical-doubles.txt", report.ToString());
    }

    private static string Append(StringBuilder report, Envelope envelope)
    {
        var canonical = Encoding.UTF8.GetString(envelope.CanonicalBytes());
        var digest = envelope.Digest();

        report.AppendLine($"## seq {envelope.Seq} — {envelope.Kind}");
        report.AppendLine($"prev_digest: {(string.IsNullOrEmpty(envelope.PrevDigest) ? "(chain anchor)" : envelope.PrevDigest)}");
        report.AppendLine($"canonical:   {canonical}");
        report.AppendLine($"digest:      {digest}");
        report.AppendLine();

        return digest;
    }
}
