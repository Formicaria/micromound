using System.Globalization;
using System.Text;
using System.Text.Json;
using Micromound.Protocol;

namespace Micromound.Drivers;

/// <summary>
/// The Pi side of the port requests (PROTOCOL.md §12): the board as a port server, the Pi's own
/// kernel as the only authority. Over the same framing the bridge uses, roles reversed — this client
/// asks, the board answers — with three requests: <c>hello</c> (what the board offers), <c>write</c>
/// (a pin's logical level) and <c>read</c> (a channel, in volts). Every request feeds the board's
/// link watchdog; a board that stops hearing from us drives every pin safe on its own, and a pin the
/// board's compiled <c>max_on_s</c> covers is released by the board whatever we say.
///
/// <para>Request bodies and response parsing are the halves the fixture <c>port-exchange.txt</c> pins:
/// the board (<c>mm_ports</c>) wrote every response in it, and this client must read each one; the
/// bodies this client sends must equal the file's <c>req:</c> lines.</para>
/// </summary>
public sealed class LinkPortsClient : IDisposable
{
    public const string HelloPath = "micromound/link/ports/hello";
    public const string WritePath = "micromound/link/ports/write";
    public const string ReadPath = "micromound/link/ports/read";

    private readonly Stream _link;
    private readonly bool _ownsLink;
    private readonly LinkFrameDecoder _decoder = new();
    private readonly object _gate = new();
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _log;
    private byte _seq;
    private Timer? _keepalive;

    public int Requests { get; private set; }
    public int Timeouts { get; private set; }

    public LinkPortsClient(Stream link, bool ownsLink = true, TimeSpan? timeout = null, Action<string>? log = null)
    {
        _link = link;
        _ownsLink = ownsLink;
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        _log = log;
    }

    // ---- request bodies (frozen by port-exchange.txt) ----

    public static string HelloBody => "{}";
    public static string WriteBody(int pin, bool level) => $"{{\"pin\":{pin.ToString(CultureInfo.InvariantCulture)},\"level\":{(level ? "true" : "false")}}}";
    public static string ReadBody(int channel) => $"{{\"channel\":{channel.ToString(CultureInfo.InvariantCulture)}}}";

    // ---- response parsing (frozen by port-exchange.txt) ----

    public static LinkPortsHello ParseHello(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var pins = new List<LinkPortsPin>();
        if (root.TryGetProperty("pins", out var pinsEl) && pinsEl.ValueKind == JsonValueKind.Array)
            foreach (var p in pinsEl.EnumerateArray())
                pins.Add(new LinkPortsPin(
                    p.GetProperty("pin").GetInt32(),
                    p.TryGetProperty("active_high", out var ah) && ah.GetBoolean(),
                    p.TryGetProperty("max_on_s", out var mo) && mo.ValueKind == JsonValueKind.Number ? mo.GetDouble() : 0,
                    p.TryGetProperty("level", out var lv) && lv.GetBoolean()));
        var channels = new List<int>();
        if (root.TryGetProperty("channels", out var chEl) && chEl.ValueKind == JsonValueKind.Array)
            foreach (var c in chEl.EnumerateArray()) channels.Add(c.GetInt32());
        return new LinkPortsHello(
            root.TryGetProperty("profile", out var pr) ? pr.GetString() ?? "" : "",
            root.TryGetProperty("firmware", out var fw) ? fw.GetString() ?? "" : "",
            root.TryGetProperty("watchdog_s", out var wd) && wd.ValueKind == JsonValueKind.Number ? wd.GetInt32() : 0,
            root.TryGetProperty("tripped", out var tr) && tr.GetBoolean(),
            pins, channels);
    }

    public static (int Pin, bool Level) ParseWrite(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return (doc.RootElement.GetProperty("pin").GetInt32(), doc.RootElement.GetProperty("level").GetBoolean());
    }

    public static (int Channel, double Volts) ParseRead(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var volts = doc.RootElement.GetProperty("volts");
        if (volts.ValueKind != JsonValueKind.Number) throw new JsonException("volts is not a number");
        return (doc.RootElement.GetProperty("channel").GetInt32(), volts.GetDouble());
    }

    public static string ParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? body : body;
        }
        catch (JsonException) { return body; }
    }

    // ---- the exchanges ----

    public LinkPortsHello Hello()
    {
        var (status, body) = Exchange(HelloPath, HelloBody);
        if (status != 200) throw new LinkPortsException(status, ParseError(body), HelloPath);
        return ParseHello(body);
    }

    /// <summary>Drives a pin's LOGICAL level (active or not); the board applies its own polarity. Throws on refusal.</summary>
    public void Write(int pin, bool level)
    {
        var (status, body) = Exchange(WritePath, WriteBody(pin, level));
        if (status != 200) throw new LinkPortsException(status, ParseError(body), WritePath);
        var (echoPin, echoLevel) = ParseWrite(body);
        if (echoPin != pin || echoLevel != level)
            throw new LinkPortsException(status, $"the board answered pin {echoPin} level {echoLevel} to a write of pin {pin} level {level}", WritePath);
    }

    /// <summary>One sample of a channel, in volts. Throws on refusal or a failed read — never returns a zero for a fault.</summary>
    public double Read(int channel)
    {
        var (status, body) = Exchange(ReadPath, ReadBody(channel));
        if (status != 200) throw new LinkPortsException(status, ParseError(body), ReadPath);
        var (echoChannel, volts) = ParseRead(body);
        if (echoChannel != channel) throw new LinkPortsException(status, $"the board answered channel {echoChannel} to a read of channel {channel}", ReadPath);
        return volts;
    }

    /// <summary>Feeds the board's watchdog on a timer, so a Pi that is merely idle does not lose its outputs.</summary>
    public void StartKeepalive(TimeSpan every)
    {
        _keepalive?.Dispose();
        _keepalive = new Timer(_ =>
        {
            try { Hello(); }
            catch (Exception ex) when (ex is IOException or LinkPortsException or TimeoutException) { _log?.Invoke($"link ports: keepalive: {ex.Message}"); }
        }, null, every, every);
    }

    private (int Status, string Body) Exchange(string path, string body)
    {
        // One exchange at a time. A caller that cannot get the link within the timeout (another exchange
        // is stuck on a silent board and a blocking stream) times out rather than queueing behind it —
        // so a safe-state write fails loudly (the host trips) instead of hanging, and the board, hearing
        // nothing, drives itself safe after its own watchdog. Fail-safe from both ends.
        if (!Monitor.TryEnter(_gate, _timeout))
        {
            Timeouts++;
            throw new TimeoutException($"the link is busy with an exchange that has not returned within {_timeout.TotalSeconds:0.#} s");
        }
        try
        {
            Requests++;
            var seq = ++_seq;
            var frame = LinkFrame.Encode(LinkFrame.Request, seq, LinkFrame.RequestPayload(path, Encoding.UTF8.GetBytes(body)));
            _link.Write(frame, 0, frame.Length);
            _link.Flush();

            var deadline = DateTime.UtcNow + _timeout;
            var one = new byte[1];
            while (DateTime.UtcNow < deadline)
            {
                int n;
                if (_link is { CanTimeout: true }) _link.ReadTimeout = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                try { n = _link.Read(one, 0, 1); }
                catch (IOException) when (DateTime.UtcNow >= deadline) { break; }
                if (n <= 0) break;
                if (_decoder.Feed(one[0]) is not { } got || got.Type != LinkFrame.Response || got.Seq != seq) continue;
                if (!LinkFrame.TryParseResponse(got.Payload, out var status, out var responseBody))
                    throw new LinkPortsException(0, "the board's response carried no status line", path);
                return (status, Encoding.UTF8.GetString(responseBody));
            }
            Timeouts++;
            throw new TimeoutException($"the board did not answer {path} within {_timeout.TotalSeconds:0.#} s");
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public void Dispose()
    {
        _keepalive?.Dispose();
        if (_ownsLink) _link.Dispose();
    }
}

public sealed record LinkPortsPin(int Pin, bool ActiveHigh, double MaxOnSeconds, bool Level);

public sealed record LinkPortsHello(string Profile, string Firmware, int WatchdogSeconds, bool Tripped, IReadOnlyList<LinkPortsPin> Pins, IReadOnlyList<int> Channels);

/// <summary>A refusal from the board, with its status and its own words.</summary>
public sealed class LinkPortsException(int status, string error, string path) : IOException($"{path}: HTTP {status}: {error}")
{
    public int Status { get; } = status;
    public string Error { get; } = error;
}

/// <summary>
/// A digital line on a board reached over the link. Writes carry the LOGICAL level; the board applies
/// polarity. The manifest's <c>active_high</c> must agree with the board's compiled one for the pin — the
/// opener checks at bring-up and refuses a mismatch, because a disagreement would energize a load at
/// the safe level.
/// </summary>
public sealed class LinkDigitalOutput : IDigitalOutput
{
    private readonly LinkPortsClient _client;
    private readonly int _pin;
    private readonly bool _activeHigh;

    public LinkDigitalOutput(LinkPortsClient client, int pin, bool activeHigh, bool initialHigh)
    {
        _client = client;
        _pin = pin;
        _activeHigh = activeHigh;
        Write(initialHigh);   // the safe level, as every backing does at bring-up
    }

    public bool State { get; private set; }

    public void Write(bool high)
    {
        _client.Write(_pin, level: high == _activeHigh);   // logical: active iff the physical level is the active one
        State = high;
    }
}

/// <summary>An analog channel on a board reached over the link, in volts; a fault throws, never reads zero.</summary>
public sealed class LinkAnalogInput(LinkPortsClient client, int channel) : IAnalogInput
{
    public double Read() => client.Read(channel);
}

/// <summary>How a factory reaches a board: one client per link path, shared by every port on that board.</summary>
public interface ILinkPortsProvider
{
    LinkPortsClient Open(string link);
}

/// <summary>
/// The default provider: opens the serial device as a plain file (raw mode is the operator's job:
/// <c>stty -F /dev/ttyUSB0 115200 raw -echo</c>), says hello, starts a keepalive at a third of the
/// board's watchdog, and hands the same client to every port on that link. Injectable for tests.
///
/// <para>A plain file stream on a tty has no read timeout: a board that goes silent mid-exchange holds
/// that one exchange until bytes arrive. Every other caller times out at the link's gate instead of
/// queueing (see <see cref="LinkPortsClient"/>), the host's loop watchdog trips the mound, and the board
/// drives its own outputs safe once it stops hearing the keepalive — the failure is loud on both ends.
/// A timed serial layer (<c>System.IO.Ports</c>) can replace the opener without touching the rest.</para>
/// </summary>
public sealed class LinkPortsPool(Func<string, Stream>? open = null, Action<string>? log = null) : ILinkPortsProvider, IDisposable
{
    private readonly Dictionary<string, LinkPortsClient> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkPortsHello> _hellos = new(StringComparer.Ordinal);
    private readonly Func<string, Stream> _open = open ?? (path => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1, FileOptions.None));

    public static LinkPortsPool Shared { get; } = new();

    public LinkPortsClient Open(string link)
    {
        lock (_clients)
        {
            if (_clients.TryGetValue(link, out var existing)) return existing;
            var client = new LinkPortsClient(_open(link), log: log);
            var hello = client.Hello();   // discovery: a board that does not answer is not a board we compose against
            _hellos[link] = hello;
            log?.Invoke($"link ports: {link}: {hello.Profile} firmware {hello.Firmware}, {hello.Pins.Count} pin(s), {hello.Channels.Count} channel(s), watchdog {hello.WatchdogSeconds} s{(hello.Tripped ? " — TRIPPED" : "")}");
            if (hello.WatchdogSeconds > 0) client.StartKeepalive(TimeSpan.FromSeconds(Math.Max(1, hello.WatchdogSeconds / 3.0)));
            _clients[link] = client;
            return client;
        }
    }

    /// <summary>What the board said at discovery, for the openers' checks.</summary>
    public LinkPortsHello Describe(string link)
    {
        Open(link);
        lock (_clients) return _hellos[link];
    }

    public void Dispose()
    {
        lock (_clients)
        {
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
        }
    }
}

/// <summary>The <c>link</c> manifest setting, shared by every hardware backing: set, the port lives on a board.</summary>
public static class LinkSettings
{
    public const string Key = "link";

    public static bool TryLink(IReadOnlyDictionary<string, string> settings, out string link)
    {
        link = "";
        if (!settings.TryGetValue(Key, out var raw) || string.IsNullOrWhiteSpace(raw)) return false;
        link = raw.Trim();
        return true;
    }

    /// <summary>A digital line on the board named by <c>link</c>: checked against what the board offers, polarity included.</summary>
    public static IDigitalOutput OpenOutput(ILinkPortsProvider links, string link, int pin, bool activeHigh)
    {
        var client = links.Open(link);
        var hello = links is LinkPortsPool pool ? pool.Describe(link) : client.Hello();
        var offered = hello.Pins.FirstOrDefault(p => p.Pin == pin)
            ?? throw new ArgumentException($"the board on {link} offers no pin {pin} (it offers {string.Join(", ", hello.Pins.Select(p => p.Pin))})");
        if (offered.ActiveHigh != activeHigh)
            throw new ArgumentException($"the board on {link} compiles pin {pin} as active-{(offered.ActiveHigh ? "high" : "low")}; the manifest says active-{(activeHigh ? "high" : "low")} — a mismatch would energize the load at the safe level");
        if (hello.Tripped)
            throw new ArgumentException($"the board on {link} is tripped (a line would not release); reboot it before composing against it");
        return new LinkDigitalOutput(client, pin, activeHigh, initialHigh: !activeHigh);
    }

    public static IAnalogInput OpenInput(ILinkPortsProvider links, string link, int channel)
    {
        var client = links.Open(link);
        var hello = links is LinkPortsPool pool ? pool.Describe(link) : client.Hello();
        if (!hello.Channels.Contains(channel))
            throw new ArgumentException($"the board on {link} offers no channel {channel} (it offers {string.Join(", ", hello.Channels)})");
        return new LinkAnalogInput(client, channel);
    }
}
