using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Micromound.Drivers;
using Micromound.Protocol;

namespace Micromound.Acceptance;

/// <summary>
/// The port-server firmware as a host process, on the other end of a real byte stream.
///
/// <para><c>firmware/micromound-c/build/mm_board_sim</c> is the C the ESP32 image is built from —
/// <c>mm_ports</c>, <c>mm_frame</c>, the same handlers, the same refusals, the same compiled
/// <c>max_on_s</c> and link watchdog — with only the world below its HAL simulated. Running it here
/// means the acceptance run exercises the firmware that ships, over the framing that ships, through
/// the Pi's own generic drivers: nothing between the mound and the pin is a C# imitation.</para>
///
/// <para>The board's clock only moves when this class tells it to (<see cref="Advance"/>), so a whole
/// run is deterministic — no sleeps, no wall clock, no flakes.</para>
/// </summary>
public sealed class BoardProcess : ILinkPortsProvider, IDisposable
{
    private readonly Process _process;
    private readonly DuplexStream _stream;
    private readonly LinkPortsClient _client;

    private BoardProcess(Process process)
    {
        _process = process;
        _stream = new DuplexStream(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        // ONE client on the stream. A second would run a second frame decoder over the same bytes and
        // the two would steal each other's answers — the link is one exchange at a time by definition,
        // so the drivers and the simulator's own control path share this client and its gate.
        _client = new LinkPortsClient(_stream, ownsLink: false, timeout: TimeSpan.FromSeconds(10));
    }

    /// <summary>The provider the driver factories resolve a manifest's <c>link</c> setting through.</summary>
    public LinkPortsClient Open(string link) => _client;

    /// <summary>What the board says it offers.</summary>
    public LinkPortsHello Hello() => _client.Hello();

    /// <summary>The link name a manifest must use to reach this board.</summary>
    public const string LinkName = "board";

    /// <summary>The built simulator, or null when this checkout has not built the C tools.</summary>
    public static string? FindBinary(string? repoRoot = null)
    {
        var dir = new DirectoryInfo(repoRoot ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "firmware", "micromound-c", "build", "mm_board_sim");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Starts the board: one output line (the valve, with the board's own 30 s bound), one input line
    /// (an active-low limit switch that closes 2 s after the valve opens — the independent observer),
    /// and one analog channel that rises while the valve is open (the tank filling).
    /// </summary>
    public static BoardProcess Start(string binary, int watchdogSeconds = 60)
    {
        var info = new ProcessStartInfo(binary)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "--profile", "acceptance-bench",
                     "--watchdog", watchdogSeconds.ToString(),
                     "--pin", "5:high:30",
                     "--input", "12:low:follows=5:delay=2",
                     "--channel", "0:0.20:rises=5@0.05"
                 })
            info.ArgumentList.Add(argument);

        var process = Process.Start(info) ?? throw new IOException($"could not start {binary}");
        return new BoardProcess(process);
    }

    /// <summary>The simulator's banner line, for the report.</summary>
    public string Banner => _process.StandardError.ReadLine() ?? "";

    /// <summary>Advances the board's clock, one simulated second at a time, and returns the world.</summary>
    public BoardWorld Advance(int seconds) => World(_client.Request(SimAdvancePath, $"{{\"seconds\":{seconds}}}"));

    /// <summary>The world as it stands: what a multimeter and a stopwatch would say.</summary>
    public BoardWorld World() => World(_client.Request(SimWorldPath, "{}"));

    /// <summary>Injects a hardware failure: a line that will not drive, will not read, or an ADC that is gone.</summary>
    public void Fault(string body) => _client.Request(SimFaultPath, body);

    /// <summary>
    /// Drives one of the board's lines directly over the link, with no mound above it — the harness
    /// standing in for a Pi that opened a line and then died. It is the only way to reach the board's
    /// own limit tier, because a working mound never lets that tier bite.
    /// </summary>
    public void Drive(int pin, bool level) => _client.Write(pin, level);

    private const string SimAdvancePath = "micromound/link/sim/advance";
    private const string SimWorldPath = "micromound/link/sim/world";
    private const string SimFaultPath = "micromound/link/sim/fault";

    private static BoardWorld World((int Status, string Body) answer)
    {
        if (answer.Status != 200) throw new IOException($"the simulator answered {answer.Status}: {answer.Body}");
        using var doc = JsonDocument.Parse(answer.Body);
        var root = doc.RootElement;
        var pins = root.GetProperty("pins").EnumerateArray()
            .Select(p => new BoardPin(p.GetProperty("pin").GetInt32(), p.GetProperty("active").GetBoolean(),
                                      p.GetProperty("level").GetBoolean(), p.GetProperty("writes").GetInt32(),
                                      p.GetProperty("auto_releases").GetInt32()))
            .ToList();
        var inputs = root.GetProperty("inputs").EnumerateArray()
            .Select(p => new BoardInput(p.GetProperty("pin").GetInt32(), p.GetProperty("level").GetBoolean(), p.GetProperty("reads").GetInt32()))
            .ToList();
        var channels = root.GetProperty("channels").EnumerateArray()
            .Select(c => new BoardChannel(c.GetProperty("channel").GetInt32(), c.GetProperty("volts").GetDouble()))
            .ToList();
        return new BoardWorld(root.GetProperty("now").GetInt64(), root.GetProperty("tripped").GetBoolean(),
            root.GetProperty("watchdog_trips").GetInt32(), pins, inputs, channels);
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already gone */ }
        _client.Dispose();
        _stream.Dispose();
        _process.Dispose();
    }

    /// <summary>A process's stdout/stdin as one stream, which is what a serial device is.</summary>
    private sealed class DuplexStream(Stream read, Stream write) : Stream
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

public sealed record BoardPin(int Pin, bool Active, bool Level, int Writes, int AutoReleases);
public sealed record BoardInput(int Pin, bool Level, int Reads);
public sealed record BoardChannel(int Channel, double Volts);

public sealed record BoardWorld(long Now, bool Tripped, int WatchdogTrips,
    IReadOnlyList<BoardPin> Pins, IReadOnlyList<BoardInput> Inputs, IReadOnlyList<BoardChannel> Channels)
{
    public BoardPin Pin(int pin) => Pins.Single(p => p.Pin == pin);
    public BoardInput Input(int pin) => Inputs.Single(p => p.Pin == pin);
    public double Volts(int channel) => Channels.Single(c => c.Channel == channel).Volts;
}
