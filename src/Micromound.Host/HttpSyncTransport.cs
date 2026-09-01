using System.Net.Http;
using System.Text;
using System.Text.Json;
using Micromound.Protocol;
using Micromound.Sync;

namespace Micromound.Host;

/// <summary>
/// The real network transport — PROTOCOL.md §1. Device-initiated: the mound POSTs one signed uplink
/// envelope to the controller at <c>&lt;base&gt;/micromound/v0/sync</c> and reads back whatever
/// downlink came with the response (charter updates, configuration, missions, stop orders). The
/// controller is never dialled from outside; the mound always initiates.
///
/// <para>Offline is a normal state, not an error (PROTOCOL.md §1). A connection failure, a timeout,
/// a non-success status, or an unreadable body all return <c>false</c> with a reason — the durable
/// uplink queue keeps the backlog and re-sends oldest-first on the next beat. Nothing here throws
/// into the sync loop.</para>
///
/// <para>This carries envelopes; it does not change them. The envelope JSON is the frozen wire form
/// (<see cref="ProtocolJson.Options"/>), so the canonical bytes and signatures are untouched — the
/// HTTP framing is transport, not protocol.</para>
/// </summary>
public sealed class HttpSyncTransport : ISyncTransport, IDisposable
{
    /// <summary>The sync path under the controller base, per PROTOCOL.md §1 (<c>/micromound/v0/*</c>).</summary>
    public const string SyncPath = "micromound/v0/sync";

    /// <summary>
    /// Cap on a single downlink response — a controller has no legitimate reason to send more, and a
    /// constrained mound must not be OOM'd by a hostile or misconfigured one. An oversize body is a
    /// failed exchange, not a crash. Only enforced on a client this transport owns; an injected client
    /// (tests) sets its own limit.
    /// </summary>
    public const long MaxResponseBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _syncEndpoint;
    private readonly TimeSpan _timeout;

    /// <param name="controllerBaseUrl">The controller root, e.g. <c>https://anthill.example/</c>. The
    /// daemon requires <c>https</c> (PROTOCOL.md §1); this class carries whatever URL it is given so a
    /// local <c>http</c> stub can exercise it in tests.</param>
    /// <param name="http">An injected client (tests share one); a private client is created when null.</param>
    /// <param name="timeout">Per-exchange timeout — a slow controller is treated as offline, not a hang.
    /// Note: <see cref="ISyncTransport"/> carries no cancellation token, so a shutdown signal is only
    /// observed BETWEEN exchanges; the timeout is what bounds an in-flight one. Kept short for that.</param>
    public HttpSyncTransport(Uri controllerBaseUrl, HttpClient? http = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(controllerBaseUrl);
        // A base without a trailing slash would drop its last path segment when combined; normalize.
        var root = controllerBaseUrl.AbsoluteUri.EndsWith('/') ? controllerBaseUrl : new Uri(controllerBaseUrl.AbsoluteUri + "/");
        _syncEndpoint = new Uri(root, SyncPath);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { MaxResponseContentBufferSize = MaxResponseBytes };
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public bool TryExchange(Envelope uplink, out IReadOnlyList<Envelope> downlink, out string detail)
    {
        ArgumentNullException.ThrowIfNull(uplink);
        downlink = [];

        try
        {
            var json = JsonSerializer.Serialize(uplink, ProtocolJson.Options);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(_timeout);

            using var response = _http.PostAsync(_syncEndpoint, content, cts.Token).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                detail = $"controller returned HTTP {(int)response.StatusCode}";
                return false;   // not offline exactly, but a failed exchange — the queue retries
            }

            var body = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            downlink = ParseDownlink(body);
            detail = $"exchanged; {downlink.Count} downlink envelope(s)";
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or OperationCanceledException or NotSupportedException or InvalidOperationException or UriFormatException)
        {
            // The controller could not be reached, did not answer in time, or the URL's scheme is one
            // HttpClient will not dial (e.g. a mis-configured ftp://). All of these are a failed
            // exchange, not a crash into the sync loop — offline is normal, the queue retries.
            detail = "offline: " + ex.Message;
            return false;
        }
        catch (JsonException ex)
        {
            // A response we cannot read is a failed exchange, not a crash — retry on the next beat.
            detail = "controller response unreadable: " + ex.Message;
            return false;
        }
    }

    private static IReadOnlyList<Envelope> ParseDownlink(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return [];   // an empty response is "nothing downlink", not an error
        return JsonSerializer.Deserialize<List<Envelope>>(body, ProtocolJson.Options) ?? [];
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
