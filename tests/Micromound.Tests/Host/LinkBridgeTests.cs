using System.Net;
using System.Text;
using Micromound.Host;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The bridge is transport: what a board frames as a request reaches the controller byte for byte,
/// what the controller answers comes back byte for byte, an unreachable controller reads as offline
/// (status 0) exactly as a Wi-Fi board would see it, the Pi's clock is the one thing the bridge
/// answers itself, and nothing outside the protocol paths is relayed.
/// </summary>
public class LinkBridgeTests
{
    private sealed class Capture(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(string Path, string Body)> Seen = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request.RequestUri!.AbsolutePath, body));
            return respond(request, body);
        }
    }

    private static readonly DateTimeOffset Clock = new(2026, 8, 14, 21, 4, 11, TimeSpan.Zero);

    private static LinkFrameData Decode(byte[] frame)
    {
        var decoder = new LinkFrameDecoder();
        LinkFrameData? got = null;
        foreach (var b in frame) got ??= decoder.Feed(b);
        Assert.NotNull(got);
        return got.Value;
    }

    [Fact]
    public void A_request_is_relayed_untouched_and_the_answer_framed_back()
    {
        var handler = new Capture((req, body) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"v\":0,\"kind\":\"ack\"}]", Encoding.UTF8, "application/json")
        });
        using var bridge = new LinkBridge(new Uri("https://anthill.local:8443/"), new HttpClient(handler), () => Clock);

        var uplink = "{\"v\":0,\"id\":\"x\",\"kind\":\"mound_sync\",\"sig\":\"ed25519:00\"}";
        var request = new LinkFrameData(LinkFrame.Request, 9, LinkFrame.RequestPayload("micromound/v0/sync", Encoding.UTF8.GetBytes(uplink)));
        var response = Decode(bridge.Handle(request));

        Assert.Single(handler.Seen);
        Assert.Equal("/micromound/v0/sync", handler.Seen[0].Path);
        Assert.Equal(uplink, handler.Seen[0].Body);                        // byte for byte: the signature still covers it
        Assert.Equal(LinkFrame.Response, response.Type);
        Assert.Equal(9, response.Seq);                                     // the answer names the request
        Assert.True(LinkFrame.TryParseResponse(response.Payload, out var status, out var body));
        Assert.Equal(200, status);
        Assert.Equal("[{\"v\":0,\"kind\":\"ack\"}]", Encoding.UTF8.GetString(body));
        Assert.Equal(1, bridge.Relayed);
    }

    [Fact]
    public void An_unreachable_controller_is_offline_not_an_error_and_a_4xx_is_a_4xx()
    {
        var calls = 0;
        var handler = new Capture((req, body) => ++calls == 1
            ? throw new HttpRequestException("connection refused")
            : new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"accepted\":false,\"reason\":\"token already used\"}") });
        using var bridge = new LinkBridge(new Uri("https://anthill.local:8443"), new HttpClient(handler), () => Clock);

        var enroll = new LinkFrameData(LinkFrame.Request, 1, LinkFrame.RequestPayload("micromound/v0/enroll", "{\"token\":\"t\"}"u8));
        var offline = Decode(bridge.Handle(enroll));
        Assert.True(LinkFrame.TryParseResponse(offline.Payload, out var status, out var body));
        Assert.Equal(0, status);                                            // the board's HAL reads this as -1: no exchange happened
        Assert.Empty(body.ToArray());
        Assert.Equal(1, bridge.Offline);

        var refused = Decode(bridge.Handle(enroll));
        Assert.True(LinkFrame.TryParseResponse(refused.Payload, out status, out body));
        Assert.Equal(409, status);                                          // a definite refusal travels as itself
        Assert.Contains("token already used", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public void The_bridge_answers_the_time_itself_and_relays_nothing_else_off_protocol()
    {
        var handler = new Capture((req, body) => new HttpResponseMessage(HttpStatusCode.OK));
        using var bridge = new LinkBridge(new Uri("https://anthill.local:8443/"), new HttpClient(handler), () => Clock);

        var time = Decode(bridge.Handle(new LinkFrameData(LinkFrame.Request, 3, LinkFrame.RequestPayload(LinkFrame.TimePath, "{}"u8))));
        Assert.True(LinkFrame.TryParseResponse(time.Payload, out var status, out var body));
        Assert.Equal(200, status);
        Assert.Equal("{\"epoch_s\":1786741451}", Encoding.ASCII.GetString(body));

        foreach (var path in new[] { "admin/reboot", "micromound/v1/sync", "micromound/v0/../../etc/passwd", "http://elsewhere/" })
        {
            var refused = Decode(bridge.Handle(new LinkFrameData(LinkFrame.Request, 4, LinkFrame.RequestPayload(path, "{}"u8))));
            Assert.True(LinkFrame.TryParseResponse(refused.Payload, out status, out _));
            Assert.Equal(404, status);
        }
        Assert.Empty(handler.Seen);                                         // nothing off-protocol reached the network
        Assert.Equal(4, bridge.Refused);

        var malformed = Decode(bridge.Handle(new LinkFrameData(LinkFrame.Request, 5, "no newline here"u8.ToArray())));
        Assert.True(LinkFrame.TryParseResponse(malformed.Payload, out status, out _));
        Assert.Equal(400, status);
    }

    [Fact]
    public void Serve_reads_frames_off_a_stream_and_writes_the_answers_back_in_order()
    {
        var handler = new Capture((req, body) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var bridge = new LinkBridge(new Uri("https://anthill.local:8443/"), new HttpClient(handler), () => Clock);

        var inbound = new MemoryStream();
        inbound.Write("junk"u8);
        inbound.Write(LinkFrame.Encode(LinkFrame.Request, 1, LinkFrame.RequestPayload("micromound/v0/sync", "{\"a\":1}"u8)));
        inbound.Write(LinkFrame.Encode(LinkFrame.Response, 1, LinkFrame.ResponsePayload(200, "ignored: a response is not ours to answer"u8)));
        inbound.Write(LinkFrame.Encode(LinkFrame.Request, 2, LinkFrame.RequestPayload(LinkFrame.TimePath, "{}"u8)));
        inbound.Position = 0;
        var outbound = new MemoryStream();
        var duplex = new Duplex(inbound, outbound);

        bridge.Serve(duplex, CancellationToken.None);

        var decoder = new LinkFrameDecoder();
        var answers = new List<LinkFrameData>();
        foreach (var b in outbound.ToArray()) if (decoder.Feed(b) is { } f) answers.Add(f);
        Assert.Equal(2, answers.Count);
        Assert.Equal(new byte[] { 1, 2 }, answers.Select(a => a.Seq).ToArray());
        Assert.Equal(2, bridge.Requests);
        Assert.Single(handler.Seen);
    }

    private sealed class Duplex(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
    }
}
