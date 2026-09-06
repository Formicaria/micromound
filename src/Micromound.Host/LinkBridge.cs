using System.Text;

namespace Micromound.Host;

/// <summary>
/// The Pi side of the Pi↔ESP32 link — PROTOCOL.md §12. Reads request frames from a byte stream (a
/// USB-serial device, a UART, a socket), performs each request's HTTPS half against the controller
/// exactly as the board would have over Wi-Fi, and frames the status and body back. It is transport,
/// not authority: it holds no key, verifies nothing, and alters no byte of what passes through — the
/// envelopes are signed end to end by the board and by the controller, and a bridge that changed one
/// would only produce something the other side refuses.
///
/// The one request it answers itself is <see cref="LinkFrame.TimePath"/>: the Pi's clock, so a board
/// with no network of its own can have one. Anything outside <c>micromound/v0/</c> is refused with 404
/// — a bridge relays the protocol, not the Pi's network.
/// </summary>
public sealed class LinkBridge : IDisposable
{
    public const string ProtocolPrefix = "micromound/v0/";

    private readonly Uri _controller;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly TimeSpan _timeout;

    public int Requests { get; private set; }
    public int Relayed { get; private set; }
    public int Offline { get; private set; }
    public int Refused { get; private set; }

    public LinkBridge(Uri controller, HttpClient? http = null, Func<DateTimeOffset>? clock = null, Action<string>? log = null, TimeSpan? timeout = null)
    {
        if (!controller.IsAbsoluteUri || controller.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("the controller must be an absolute https:// URL", nameof(controller));
        _controller = controller.AbsoluteUri.EndsWith('/') ? controller : new Uri(controller.AbsoluteUri + "/");
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Serves one stream until it ends or the token cancels. Frames that are not requests are ignored.</summary>
    public void Serve(Stream link, CancellationToken ct)
    {
        var decoder = new LinkFrameDecoder();
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = link.Read(buffer, 0, buffer.Length); }
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }
            if (n <= 0) break;
            for (var i = 0; i < n; i++)
            {
                if (decoder.Feed(buffer[i]) is not { } frame || frame.Type != LinkFrame.Request) continue;
                var response = Handle(frame);
                try { link.Write(response, 0, response.Length); link.Flush(); }
                catch (IOException) { return; }
            }
        }
    }

    /// <summary>Answers one request frame with the response frame to send back. Public so a test can drive it without a stream.</summary>
    public byte[] Handle(LinkFrameData request)
    {
        Requests++;
        if (!LinkFrame.TryParseRequest(request.Payload, out var path, out var body))
        {
            Refused++;
            return LinkFrame.Encode(LinkFrame.Response, request.Seq, LinkFrame.ResponsePayload(400, "malformed request payload"u8));
        }

        if (string.Equals(path, LinkFrame.TimePath, StringComparison.Ordinal))
        {
            var epoch = _clock().ToUnixTimeSeconds();
            _log?.Invoke($"link: {path} -> {epoch}");
            return LinkFrame.Encode(LinkFrame.Response, request.Seq, LinkFrame.ResponsePayload(200, Encoding.ASCII.GetBytes($"{{\"epoch_s\":{epoch}}}")));
        }

        if (!path.StartsWith(ProtocolPrefix, StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
        {
            Refused++;
            _log?.Invoke($"link: refused '{path}': not a protocol path");
            return LinkFrame.Encode(LinkFrame.Response, request.Seq, LinkFrame.ResponsePayload(404, "not a protocol path"u8));
        }

        var (status, responseBody) = Relay(path, body.ToArray());
        if (status == 0) Offline++; else Relayed++;
        _log?.Invoke($"link: {path} ({body.Length} B) -> {(status == 0 ? "offline" : status.ToString())} ({responseBody.Length} B)");
        return LinkFrame.Encode(LinkFrame.Response, request.Seq, LinkFrame.ResponsePayload(status, responseBody));
    }

    private (int Status, byte[] Body) Relay(string path, byte[] body)
    {
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var cts = new CancellationTokenSource(_timeout);
            using var response = _http.PostAsync(new Uri(_controller, path), content, cts.Token).GetAwaiter().GetResult();
            var bytes = response.Content.ReadAsByteArrayAsync(cts.Token).GetAwaiter().GetResult();
            // A body the frame cannot carry is a failed exchange, not a truncated one the board would misread.
            if (bytes.Length > LinkFrame.MaxPayload - 4) return (0, []);
            return ((int)response.StatusCode, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return (0, []);   // no exchange happened: offline, as the board's HAL defines it
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
