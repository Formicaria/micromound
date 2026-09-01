using System.Net;
using System.Text;
using Micromound.Host;
using Micromound.Protocol;
using Micromound.Sync;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// Enrollment — PROTOCOL.md §3. The device presents its one-time token and its public key and
/// receives the controller's key, which it persists so later boots skip enrollment (a burned token
/// cannot be reused). A refusal is definite; an unreachable controller just means "not yet". The
/// HTTP client is stubbed through an injected handler, and the resolve/persist logic through a fake
/// enrollment client — both run anywhere with no socket.
/// </summary>
public sealed class EnrollmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mm-enrolltest-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Controller = new("https://controller.test/");

    private static readonly byte[] DeviceKey = BuildKey(0);
    private static readonly byte[] ControllerKey = BuildKey(150);
    private static byte[] BuildKey(int start) { var k = new byte[32]; for (var i = 0; i < 32; i++) k[i] = (byte)(start + i); return k; }

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

    private sealed class FakeEnroll(Func<string, byte[], (bool ok, byte[] key, string detail)> f) : IEnrollmentClient
    {
        public int Calls { get; private set; }
        public bool TryEnroll(string token, byte[] devicePublicKey, out byte[] controllerPublicKey, out string detail)
        {
            Calls++;
            var (ok, key, d) = f(token, devicePublicKey);
            controllerPublicKey = key; detail = d; return ok;
        }
    }

    [Fact]
    public void Http_enroll_posts_the_token_and_key_and_receives_the_controller_key()
    {
        var ctlHex = Convert.ToHexString(ControllerKey).ToLowerInvariant();
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"controller_public_key\":\"{ctlHex}\"}}", Encoding.UTF8, "application/json")
        });
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler));

        var ok = client.TryEnroll("tok-123", DeviceKey, out var controllerKey, out _);

        Assert.True(ok);
        Assert.Equal(ControllerKey, controllerKey);
        Assert.Equal("/micromound/v0/enroll", handler.LastPath);
        Assert.Contains("\"token\":\"tok-123\"", handler.LastBody!);
        Assert.Contains(Convert.ToHexString(DeviceKey).ToLowerInvariant(), handler.LastBody!);
    }

    [Fact]
    public void A_4xx_is_a_definite_refusal()
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
    public void Resolve_enrolls_once_persists_and_reloads_without_re_enrolling()
    {
        var fake = new FakeEnroll((token, _) => token == "good"
            ? (true, ControllerKey, "ok")
            : (false, [], "enrollment refused: burned"));

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
    public void A_burned_token_leaves_the_mound_un_enrolled()
    {
        var fake = new FakeEnroll((_, _) => (false, [], "enrollment refused: burned"));

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
    public void A_wrong_length_controller_key_is_rejected_not_trusted()
    {
        // A malformed key would verify nothing yet persist forever — a permanent brick. Reject it.
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"controller_public_key\":\"dead\"}", Encoding.UTF8, "application/json")
        });
        using var client = new HttpEnrollmentClient(Controller, new HttpClient(handler));

        var ok = client.TryEnroll("tok", DeviceKey, out _, out var detail);

        Assert.False(ok);
        Assert.Contains("invalid public key", detail);
    }

    [Fact]
    public void A_corrupt_stored_key_with_a_fresh_token_recovers_by_re_enrolling()
    {
        // A corrupt controller.pub must not block recovery: given a fresh token, clear it and enroll.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "controller.pub"), "not-hex-garbage");
        var fake = new FakeEnroll((_, _) => (true, ControllerKey, "ok"));

        var directory = MoundHost.ResolveControllerKeys(_dir, fake, DeviceKey, "good", out var enrolled, out _);

        Assert.True(enrolled);
        Assert.Equal(1, fake.Calls);
        Assert.True(directory.TryGetPublicKey(KeyIds.Controller, out var key) && key.SequenceEqual(ControllerKey));
    }
}
