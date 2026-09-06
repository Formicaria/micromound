using System.Text;
using Micromound.Host;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// Freezes the Pi↔ESP32 link framing (PROTOCOL.md §12): the CRC-32, and the exact bytes of a set of
/// request and response frames. <c>firmware/micromound-c</c>'s <c>mm_frame</c> must encode each case to
/// the same frame and decode each frame to the same payload, so a board and a bridge never disagree
/// about a byte stream.
/// </summary>
public class LinkFramesTests
{
    [Fact]
    public void The_link_framing_is_frozen()
    {
        var report = new StringBuilder();
        report.AppendLine("# MICROMOUND link frames — golden fixture");
        report.AppendLine("#");
        report.AppendLine("# Frozen by tests/Micromound.Tests/Golden/LinkFramesTests.cs from Micromound.Protocol.LinkFrame (PROTOCOL.md §12).");
        report.AppendLine("# frame = \"MM\" ver(1) type(1) seq(1) len(2 LE) payload crc32(4 LE); crc32 = IEEE 802.3 over magic..payload.");
        report.AppendLine("# The C mm_frame must encode every case to `frame:` and decode `frame:` back to `payload:`.");
        report.AppendLine();
        report.AppendLine($"crc32:     313233343536373839 -> {LinkFrame.Crc32("123456789"u8):x8}");
        report.AppendLine($"crc32:     (empty) -> {LinkFrame.Crc32(ReadOnlySpan<byte>.Empty):x8}");
        report.AppendLine();

        void Request(string label, byte seq, string path, string body)
        {
            var payload = LinkFrame.RequestPayload(path, Encoding.UTF8.GetBytes(body));
            var frame = LinkFrame.Encode(LinkFrame.Request, seq, payload);
            report.AppendLine($"## {label}");
            report.AppendLine($"type:      request");
            report.AppendLine($"seq:       {seq}");
            report.AppendLine($"path:      {path}");
            report.AppendLine($"body:      {body}");
            report.AppendLine($"payload:   {Convert.ToHexStringLower(payload)}");
            report.AppendLine($"frame:     {Convert.ToHexStringLower(frame)}");
            report.AppendLine();
        }

        void Response(string label, byte seq, int status, string body)
        {
            var payload = LinkFrame.ResponsePayload(status, Encoding.UTF8.GetBytes(body));
            var frame = LinkFrame.Encode(LinkFrame.Response, seq, payload);
            report.AppendLine($"## {label}");
            report.AppendLine($"type:      response");
            report.AppendLine($"seq:       {seq}");
            report.AppendLine($"status:    {status}");
            report.AppendLine($"body:      {body}");
            report.AppendLine($"payload:   {Convert.ToHexStringLower(payload)}");
            report.AppendLine($"frame:     {Convert.ToHexStringLower(frame)}");
            report.AppendLine();
        }

        Request("a beat", 1, "micromound/v0/sync", "{\"v\":0,\"kind\":\"mound_sync\"}");
        Request("an enrollment", 2, "micromound/v0/enroll", "{\"token\":\"tok-0001\"}");
        Request("the time", 3, LinkFrame.TimePath, "{}");
        Request("an empty body", 255, "micromound/v0/sync", "");
        Response("nothing downlink", 1, 200, "");
        Response("one envelope down", 1, 200, "[{\"v\":0,\"kind\":\"ack\"}]");
        Response("the time", 3, 200, "{\"epoch_s\":1786741451}");
        Response("offline: the bridge could not exchange", 4, 0, "");
        Response("refused: not a protocol path", 5, 404, "not a protocol path");
        Response("a controller error", 6, 500, "{\"error\":\"db\"}");
        Response("seq wraps", 0, 200, "[]");

        GoldenFile.Verify("link-frames.txt", report.ToString());
    }

    [Fact]
    public void The_decoder_drops_and_counts_what_it_must_and_resynchronises()
    {
        var good = LinkFrame.Encode(LinkFrame.Request, 7, LinkFrame.RequestPayload("micromound/v0/sync", "{}"u8));
        var decoder = new LinkFrameDecoder();

        // garbage, then a good frame
        foreach (var b in "xx?"u8) Assert.Null(decoder.Feed(b));
        LinkFrameData? got = null;
        foreach (var b in good) got ??= decoder.Feed(b);
        Assert.NotNull(got);
        Assert.Equal(7, got.Value.Seq);
        Assert.True(LinkFrame.TryParseRequest(got.Value.Payload, out var path, out _) && path == "micromound/v0/sync");

        // a corrupted CRC is dropped and counted; the frame after it still decodes
        var bad = (byte[])good.Clone(); bad[^1] ^= 0xFF;
        got = null;
        foreach (var b in bad) got ??= decoder.Feed(b);
        Assert.Null(got);
        Assert.Equal(1, decoder.DroppedCrc);
        foreach (var b in good) got ??= decoder.Feed(b);
        Assert.NotNull(got);

        // the wrong version and an oversize length are dropped at the header
        var wrongVersion = (byte[])good.Clone(); wrongVersion[2] = 2;
        foreach (var b in wrongVersion) Assert.Null(decoder.Feed(b));
        Assert.Equal(1, decoder.DroppedVersion);
        var oversize = (byte[])good.Clone(); oversize[5] = 0xFF; oversize[6] = 0xFF;
        foreach (var b in oversize) Assert.Null(decoder.Feed(b));
        Assert.Equal(1, decoder.DroppedOversize);

        // a stream joined mid-frame: the tail of one frame, then a whole one
        got = null;
        foreach (var b in good.AsSpan(10)) got ??= decoder.Feed(b);
        foreach (var b in good) got ??= decoder.Feed(b);
        Assert.NotNull(got);
        Assert.Equal(3, decoder.Frames);

        // a lone magic byte before a frame costs one version drop and one resync, never the frame
        got = null;
        decoder.Feed((byte)'M');
        foreach (var b in good) got ??= decoder.Feed(b);
        Assert.NotNull(got);
        Assert.Equal(2, decoder.DroppedVersion);
    }
}
