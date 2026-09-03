using System.Net;
using System.Text;
using System.Text.Json;
using Micromound.Host;
using Micromound.Protocol;
using Micromound.Sync;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// Enrollment — PROTOCOL.md §3. The device presents its one-time token, its public key, and what it
/// is (mound id, tier, capabilities, protocol version); it receives the controller's key — which it
/// persists so later boots skip enrollment (a burned token cannot be reused) — plus what the
/// controller tells it about its place in the colony, chiefly the sync cadence. A refusal is definite
/// and carries the controller's reason; an unreachable controller just means "not yet". The HTTP
/// client is stubbed through an injected handler, and the resolve/persist logic through a fake
/// enrollment client — both run anywhere with no socket. The request/response shapes here are the
/// ones the reference controller (ANTHILL's <c>/micromound/v0/enroll</c>) actually speaks.
/// </summary>
public sealed class EnrollmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mm-enrolltest-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Controller = new("https://controller.test/");

    private static readonly byte[] DeviceKey = BuildKey(0);
    private static readonly byte[] ControllerKey = BuildKey(150);
    private static byte[] BuildKey(int start) { var k = new byte[32]; for (var i = 0; i < 32; i++) k[i] = (byte)(start + i); return k; }
    private static string CtlHex => Convert.ToHexString(ControllerKey).ToLowerInvariant();

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeEnroll(Func<string, byte[], (bool ok, ControllerEnrollment? enrollment, string detail)> f) : IEnrollmentClient
    {
        public int Calls { get; private set; }
        public bool TryEnroll(string token, byte[] devicePublicKey, out ControllerEnrollment? enrollment, out string detail)
        {
            Calls++;
            var (ok, e, d) = f(token, devicePublicKey);
            enrollment = e; detail = d; return ok;
        }
    }

    // --- The request: what the device says about itself ---

    [Fact]
    public void Http_enroll_posts_the_token_and_key_and_receives_the_controller_key()
    {
        var handler = new StubHandler(() => Json(HttpStatusCode.OK, $"{{\"controller_public_key\":\"{CtlHex}\"}}"));
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler));

        var ok = client.TryEnroll("tok-123", DeviceKey, out var enrollment, out _);

        Assert.True(ok);
        Assert.Equal(ControllerKey, enrollment!.ControllerPublicKey);
        Assert.Equal("/micromound/v0/enroll", handler.LastPath);
        Assert.Contains("\"token\":\"tok-123\"", handler.LastBody!);
        Assert.Contains(Convert.ToHexString(DeviceKey).ToLowerInvariant(), handler.LastBody!);
    }

    [Fact]
    public void The_request_carries_mound_id_tier_capabilities_and_protocol_version()
    {
        // Each of these is a field the reference controller reads: mound_id as a cross-check against
        // the token, tier against its known set, capabilities for the fleet view, and protocol_version
        // so a skew is refused at the door rather than defaulted away.
        var handler = new StubHandler(() => Json(HttpStatusCode.OK, $"{{\"controller_public_key\":\"{CtlHex}\"}}"));
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler),
            hardwareProfile: "sense.soil_moisture,act.water_valve",
            moundId: "mm-greenhouse-01",
            capabilities: ["sense.soil_moisture", "act.water_valve"]);

        client.TryEnroll("tok", DeviceKey, out _, out _);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        Assert.Equal("mm-greenhouse-01", root.GetProperty("mound_id").GetString());
        Assert.Equal(ControllerTiers.EdgeQueen, root.GetProperty("tier").GetString());          // the default tier
        Assert.Equal(ProtocolVersion.Current, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal("sense.soil_moisture,act.water_valve", root.GetProperty("hardware_profile").GetString());
        var caps = root.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["sense.soil_moisture", "act.water_valve"], caps);
    }

    [Fact]
    public void The_default_tier_is_one_the_controller_accepts_and_an_unknown_tier_is_refused_at_construction()
    {
        // The reference controller refuses any tier outside its known set. "mound_major" — the old
        // hardcoded value — was such a tier, and every real enrollment was refused for it.
        Assert.True(ControllerTiers.IsKnown(ControllerTiers.EdgeQueen));
        Assert.True(ControllerTiers.IsKnown(ControllerTiers.DeterministicController));
        Assert.False(ControllerTiers.IsKnown("mound_major"));

        Assert.Throws<ArgumentException>(() =>
            new HttpEnrollmentClient(Controller, new HttpClient(new StubHandler(() => new HttpResponseMessage())), tier: "mound_major"));
    }

    // --- The response: what the controller tells the device about itself ---

    [Fact]
    public void The_full_response_is_parsed_into_the_enrollment()
    {
        var handler = new StubHandler(() => Json(HttpStatusCode.OK,
            $"{{\"accepted\":true,\"controller_public_key\":\"{CtlHex}\",\"mound_id\":\"mm-1\"," +
            "\"colony_version\":\"0.3.8.118\",\"protocol_version\":0,\"sync_interval_s\":15}"));
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler), moundId: "mm-1");

        var ok = client.TryEnroll("tok", DeviceKey, out var enrollment, out var detail);

        Assert.True(ok);
        Assert.Equal(ControllerKey, enrollment!.ControllerPublicKey);
        Assert.Equal("mm-1", enrollment.MoundId);
        Assert.Equal(15, enrollment.SyncIntervalSeconds);
        Assert.Equal(0, enrollment.ProtocolVersion);
        Assert.Equal("0.3.8.118", enrollment.ColonyVersion);
        Assert.Contains("15s sync cadence", detail);
    }

    [Fact]
    public void A_key_only_response_from_an_older_controller_still_enrolls()
    {
        // Every field but the key is optional on the wire; the device falls back to local config.
        var handler = new StubHandler(() => Json(HttpStatusCode.OK, $"{{\"controller_public_key\":\"{CtlHex}\"}}"));
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler), moundId: "mm-1");

        Assert.True(client.TryEnroll("tok", DeviceKey, out var enrollment, out _));
        Assert.Null(enrollment!.SyncIntervalSeconds);
        Assert.Equal("", enrollment.MoundId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_sync_interval_is_ignored_not_adopted(int bad)
    {
        // A zero or negative cadence is not a cadence; the device enrolls and keeps its local one.
        var handler = new StubHandler(() => Json(HttpStatusCode.OK,
            $"{{\"controller_public_key\":\"{CtlHex}\",\"sync_interval_s\":{bad}}}"));
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler));

        var ok = client.TryEnroll("tok", DeviceKey, out var enrollment, out _);

        Assert.True(ok);
        Assert.Null(enrollment!.SyncIntervalSeconds);
    }

    [Fact]
    public void A_controller_that_bound_the_key_to_a_different_mound_is_refused()
    {
        // The device signs every uplink with its manifest id. A key bound under another id would make
        // every beat unattributable — refuse now, naming both, rather than fail mysteriously later.
        var handler = new StubHandler(() => Json(HttpStatusCode.OK,
            $"{{\"controller_public_key\":\"{CtlHex}\",\"mound_id\":\"mm-other\"}}"));
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler), moundId: "mm-mine");

        var ok = client.TryEnroll("tok", DeviceKey, out _, out var detail);

        Assert.False(ok);
        Assert.Contains("mm-other", detail);
        Assert.Contains("mm-mine", detail);
    }

    [Fact]
    public void A_controller_on_a_different_protocol_version_is_refused()
    {
        var handler = new StubHandler(() => Json(HttpStatusCode.OK,
            $"{{\"controller_public_key\":\"{CtlHex}\",\"protocol_version\":{ProtocolVersion.Current + 1}}}"));
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler));

        var ok = client.TryEnroll("tok", DeviceKey, out _, out var detail);

        Assert.False(ok);
        Assert.Contains("protocol version", detail);
    }

    // --- Refusals and reachability ---

    [Fact]
    public void A_4xx_is_a_definite_refusal_and_carries_the_controllers_reason()
    {
        // The reference controller answers a refusal with {accepted:false, reason}. The reason is what
        // tells an operator standing next to the hardware WHY — a wrong tier, a burned token.
        var handler = new StubHandler(() => Json(HttpStatusCode.BadRequest,
            "{\"accepted\":false,\"reason\":\"unknown tier 'mound_major'\"}"));
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler));

        var ok = client.TryEnroll("tok", DeviceKey, out _, out var detail);

        Assert.False(ok);
        Assert.Contains("refused", detail);
        Assert.Contains("unknown tier 'mound_major'", detail);
    }

    [Fact]
    public void A_4xx_with_no_body_is_still_a_definite_refusal()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler));

        var ok = client.TryEnroll("burned", DeviceKey, out _, out var detail);

        Assert.False(ok);
        Assert.Contains("refused", detail);
    }

    [Fact]
    public void An_unreachable_controller_is_not_enrolled_yet_not_a_crash()
    {
        var handler = new StubHandler(() => throw new HttpRequestException("connection refused"));
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler));

        var ok = client.TryEnroll("tok", DeviceKey, out _, out var detail);

        Assert.False(ok);
        Assert.Contains("unreachable", detail);
    }

    [Fact]
    public void A_wrong_length_controller_key_is_rejected_not_trusted()
    {
        // A malformed key would verify nothing yet persist forever — a permanent brick. Reject it.
        var handler = new StubHandler(() => Json(HttpStatusCode.OK, "{\"controller_public_key\":\"dead\"}"));
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler));

        var ok = client.TryEnroll("tok", DeviceKey, out _, out var detail);

        Assert.False(ok);
        Assert.Contains("invalid public key", detail);
    }

    // --- Resolve + persist across boots ---

    [Fact]
    public void Resolve_enrolls_once_persists_and_reloads_without_re_enrolling()
    {
        var fake = new FakeEnroll((token, _) => token == "good"
            ? (true, new ControllerEnrollment(ControllerKey), "ok")
            : (false, null, "enrollment refused: burned"));

        var first = MoundHost.ResolveControllerKeys(_dir, fake, DeviceKey, "good", out var enrolled1, out _);
        Assert.True(enrolled1);
        Assert.True(first.TryGetPublicKey(KeyIds.Controller, out var key) && key.SequenceEqual(ControllerKey));
        Assert.True(File.Exists(Path.Combine(_dir, "controller.pub")));

        // A second boot must load the stored key, not present the (now burned) token again.
        MoundHost.ResolveControllerKeys(_dir, fake, DeviceKey, "good", out var enrolled2, out _);
        Assert.True(enrolled2);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void The_sync_cadence_is_persisted_beside_the_key_and_survives_a_reboot()
    {
        // Enrollment happens once per token; a cadence learned then and not persisted would be lost on
        // the first reboot and never learned again.
        var fake = new FakeEnroll((_, _) => (true, new ControllerEnrollment(ControllerKey, "mm-1", SyncIntervalSeconds: 20), "ok"));

        var boot1 = MoundHost.ResolveControllerLink(_dir, fake, DeviceKey, "good");
        Assert.True(boot1.Enrolled);
        Assert.Equal(20, boot1.SyncIntervalSeconds);
        Assert.True(File.Exists(Path.Combine(_dir, "controller.meta.json")));

        var boot2 = MoundHost.ResolveControllerLink(_dir, fake, DeviceKey, token: null);   // no token: must reload
        Assert.True(boot2.Enrolled);
        Assert.Equal(20, boot2.SyncIntervalSeconds);   // the cadence came back from the sidecar
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void An_older_state_directory_with_only_the_key_file_still_loads()
    {
        // Additive: a controller.pub written before the sidecar existed loads with no cadence.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "controller.pub"), CtlHex);

        var link = MoundHost.ResolveControllerLink(_dir, enrollment: null, DeviceKey, token: null);

        Assert.True(link.Enrolled);
        Assert.Null(link.SyncIntervalSeconds);
    }

    [Fact]
    public void A_corrupt_sidecar_costs_only_the_cadence_never_the_key()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "controller.pub"), CtlHex);
        File.WriteAllText(Path.Combine(_dir, "controller.meta.json"), "{not json");

        var link = MoundHost.ResolveControllerLink(_dir, enrollment: null, DeviceKey, token: null);

        Assert.True(link.Enrolled);                    // the key is what matters, and it loaded
        Assert.Null(link.SyncIntervalSeconds);
    }

    [Fact]
    public void A_burned_token_leaves_the_mound_un_enrolled()
    {
        var fake = new FakeEnroll((_, _) => (false, null, "enrollment refused: burned"));

        MoundHost.ResolveControllerKeys(_dir, fake, DeviceKey, "bad", out var enrolled, out var detail);

        Assert.False(enrolled);
        Assert.Contains("burned", detail);
    }

    [Fact]
    public void No_token_and_no_stored_key_is_un_enrolled_the_safe_direction()
    {
        MoundHost.ResolveControllerKeys(_dir, enrollment: null, DeviceKey, token: null, out var enrolled, out _);
        Assert.False(enrolled);
    }

    [Fact]
    public void A_corrupt_stored_key_with_a_fresh_token_recovers_by_re_enrolling()
    {
        // A corrupt controller.pub must not block recovery: given a fresh token, clear it and enroll.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "controller.pub"), "not-hex-garbage");
        var fake = new FakeEnroll((_, _) => (true, new ControllerEnrollment(ControllerKey), "ok"));

        var directory = MoundHost.ResolveControllerKeys(_dir, fake, DeviceKey, "good", out var enrolled, out _);

        Assert.True(enrolled);
        Assert.Equal(1, fake.Calls);
        Assert.True(directory.TryGetPublicKey(KeyIds.Controller, out var key) && key.SequenceEqual(ControllerKey));
    }
}
