using System.Net;
using System.Text;
using System.Text.Json;
using Micromound.Host;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The real network transport (PROTOCOL.md §1): the mound POSTs one signed envelope to
/// <c>/micromound/v0/sync</c> and reads the downlink from the response. Offline — an unreachable
/// controller, a bad status, an unreadable body — is a normal, non-throwing failed exchange, so the
/// durable queue retries. Stubbed through an injected message handler, so these run anywhere with no
/// real socket.
/// </summary>
public sealed class HttpSyncTransportTests
{
    private static readonly Uri Controller = new("https://controller.test/");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    private static Envelope Env(string kind, long seq) => new()
    {
        MoundId = "mm-http", Seq = seq, Kind = kind, SentAt = Now.ToWire(),
        Body = JsonSerializer.SerializeToElement(new { k = kind }, ProtocolJson.Options)
    };

    /// <summary>A message handler that captures the request and returns a scripted response.</summary>
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

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpSyncTransport Transport(StubHandler handler) =>
        new(Controller, new HttpClient(handler));

    [Fact]
    public void It_posts_the_uplink_to_the_sync_endpoint_and_returns_the_downlink()
    {
        var downlink = JsonSerializer.Serialize(new List<Envelope> { Env(EnvelopeKinds.Charter, 1) }, ProtocolJson.Options);
        var handler = new StubHandler(() => Json(HttpStatusCode.OK, downlink));
        var transport = Transport(handler);

        var ok = transport.TryExchange(Env(EnvelopeKinds.MoundSync, 5), out var received, out _);

        Assert.True(ok);
        Assert.Equal("/micromound/v0/sync", handler.LastPath);   // PROTOCOL.md §1
        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"seq\":5", handler.LastBody!);          // the uplink went out as its wire JSON
        var envelope = Assert.Single(received);
        Assert.Equal(EnvelopeKinds.Charter, envelope.Kind);
    }

    [Fact]
    public void An_unreachable_controller_is_offline_not_a_crash()
    {
        var handler = new StubHandler(() => throw new HttpRequestException("connection refused"));
        var transport = Transport(handler);

        var ok = transport.TryExchange(Env(EnvelopeKinds.MoundSync, 1), out var received, out var detail);

        Assert.False(ok);
        Assert.Empty(received);
        Assert.Contains("offline", detail);
    }

    [Fact]
    public void A_controller_error_status_is_a_failed_exchange()
    {
        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var transport = Transport(handler);

        var ok = transport.TryExchange(Env(EnvelopeKinds.MoundSync, 1), out _, out var detail);

        Assert.False(ok);
        Assert.Contains("503", detail);
    }

    [Fact]
    public void An_unreadable_response_is_a_failed_exchange_not_a_crash()
    {
        var handler = new StubHandler(() => Json(HttpStatusCode.OK, "{not json"));
        var transport = Transport(handler);

        var ok = transport.TryExchange(Env(EnvelopeKinds.MoundSync, 1), out _, out var detail);

        Assert.False(ok);
        Assert.Contains("unreadable", detail);
    }

    [Fact]
    public void A_scheme_httpclient_cannot_dial_is_a_failed_exchange_not_a_crash()
    {
        // A mis-configured controller URL (e.g. ftp://) makes HttpClient throw NotSupportedException;
        // it must be caught as a failed exchange, never propagate into the sync loop and crash.
        using var transport = new HttpSyncTransport(new Uri("ftp://controller.test/"));

        var ok = transport.TryExchange(Env(EnvelopeKinds.MoundSync, 1), out var received, out var detail);

        Assert.False(ok);
        Assert.Empty(received);
        Assert.Contains("offline", detail);
    }

    [Fact]
    public void An_empty_response_is_a_successful_exchange_with_no_downlink()
    {
        var handler = new StubHandler(() => Json(HttpStatusCode.OK, ""));
        var transport = Transport(handler);

        var ok = transport.TryExchange(Env(EnvelopeKinds.MoundSync, 1), out var received, out _);

        Assert.True(ok);
        Assert.Empty(received);
    }
}
