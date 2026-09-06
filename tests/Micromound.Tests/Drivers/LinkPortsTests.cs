using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Micromound.Capabilities;
using Micromound.Drivers;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The Pi side of the port requests (PROTOCOL.md §12): the client reads every answer the C port
/// server wrote into <c>port-exchange.txt</c> and sends the bodies that file shows; over a fake
/// board it drives logical levels with the board's polarity, refuses a polarity mismatch at
/// bring-up, treats a silent board as a timeout, and composes through the hardware factories by the
/// <c>link</c> setting alone.
/// </summary>
public class LinkPortsTests
{
    // ---- the fixture the C board wrote ----

    private static string FixturePath([System.Runtime.CompilerServices.CallerFilePath] string callerFile = "") =>
        Path.Combine(Path.GetDirectoryName(callerFile)!, "..", "Golden", "files", "port-exchange.txt");

    private static List<(string Path, string ReqBody, int Status, string RespBody)> LoadExchange()
    {
        var pairs = new List<(string, string, int, string)>();
        string? reqPath = null, reqBody = null;
        foreach (var raw in File.ReadAllLines(FixturePath()))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith('#') || line.Length == 0) continue;
            if (line.StartsWith("req:  ", StringComparison.Ordinal))
            {
                var rest = line[6..];
                var sp = rest.IndexOf(' ');
                reqPath = sp < 0 ? rest : rest[..sp];
                reqBody = sp < 0 ? "" : rest[(sp + 1)..];
            }
            else if (line.StartsWith("resp: ", StringComparison.Ordinal))
            {
                var rest = line[6..];
                var sp = rest.IndexOf(' ');
                pairs.Add((reqPath!, reqBody!, int.Parse(rest[..sp]), rest[(sp + 1)..]));
            }
        }
        return pairs;
    }

    [Fact]
    public void Every_answer_the_board_wrote_is_one_this_client_reads_and_every_body_it_sends_is_one_the_board_read()
    {
        var exchange = LoadExchange();
        Assert.True(exchange.Count >= 25, $"only {exchange.Count} exchanges in the fixture");
        var statuses = new HashSet<int>();
        var readPinLevels = new List<bool>();

        foreach (var (path, reqBody, status, respBody) in exchange)
        {
            statuses.Add(status);
            if (status == 200)
            {
                switch (path)
                {
                    case LinkPortsClient.HelloPath:
                        var hello = LinkPortsClient.ParseHello(respBody);
                        Assert.Equal("bench-1", hello.Firmware);
                        Assert.Equal(60, hello.WatchdogSeconds);
                        Assert.Contains(hello.Pins, p => p.Pin == 5 && p.ActiveHigh && p.MaxOnSeconds == 30);
                        Assert.Contains(hello.Pins, p => p.Pin == 6 && !p.ActiveHigh && p.MaxOnSeconds == 0);
                        Assert.Contains(hello.Inputs, p => p.Pin == 12 && !p.ActiveHigh);
                        Assert.Equal([0], hello.Channels);
                        Assert.True(reqBody is "{}" or "");
                        break;
                    case LinkPortsClient.WritePath:
                        var (pin, level) = LinkPortsClient.ParseWrite(respBody);
                        // the client sends exactly this body for that pin and level (the fixture's odd bodies are the board's refusal cases)
                        using (var doc = JsonDocument.Parse(reqBody))
                            if (doc.RootElement.EnumerateObject().Count() == 2)
                                Assert.Equal(reqBody, LinkPortsClient.WriteBody(pin, level));
                        break;
                    case LinkPortsClient.ReadPath:
                        var (channel, volts) = LinkPortsClient.ParseRead(respBody);
                        Assert.Equal(0, channel);
                        Assert.Equal(0.75, volts);
                        Assert.Equal(reqBody, LinkPortsClient.ReadBody(channel));
                        break;
                    case LinkPortsClient.ReadPinPath:
                        var (inputPin, asserted) = LinkPortsClient.ParseReadPin(respBody);
                        Assert.Equal(12, inputPin);
                        Assert.Equal(reqBody, LinkPortsClient.ReadPinBody(inputPin));
                        readPinLevels.Add(asserted);
                        break;
                    default:
                        Assert.Fail($"a 200 for an unknown path {path}");
                        break;
                }
            }
            else
            {
                var error = LinkPortsClient.ParseError(respBody);
                Assert.False(string.IsNullOrWhiteSpace(error));
                Assert.NotEqual(respBody, error);   // the board always says why, as {"error":…}
            }
        }
        Assert.Equal(new[] { 200, 400, 404, 409, 503 }.Order(), statuses.Order());
        Assert.Contains(exchange, e => e.Status == 409 && e.RespBody.Contains("tripped"));
        Assert.Contains(exchange, e => e.Status == 503 && e.RespBody.Contains("sensor read failed"));
        // The switch was read open and closed, and a line that would not read was a fault, not a "false".
        Assert.Contains(true, readPinLevels);
        Assert.Contains(false, readPinLevels);
        Assert.Contains(exchange, e => e.Status == 503 && e.RespBody.Contains("the line could not be read"));
        Assert.Contains(exchange, e => e.Status == 404 && e.RespBody.Contains("is not an input of this board"));
    }

    // ---- a fake board over an in-memory duplex ----

    private sealed class QueueStream(BlockingCollection<byte> inbound, BlockingCollection<byte> outbound) : Stream
    {
        public int ReadTimeoutMs { get; set; } = 500;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override bool CanTimeout => true;
        public override int ReadTimeout { get => ReadTimeoutMs; set => ReadTimeoutMs = value; }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!inbound.TryTake(out var b, ReadTimeoutMs)) return 0;
            buffer[offset] = b;
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { for (var i = 0; i < count; i++) outbound.Add(buffer[offset + i]); }
    }

    /// <summary>A board with pin 5 (active-high, 30 s), pin 6 (active-low) and channel 0 at 0.75 V; it answers like mm_ports does.</summary>
    private sealed class FakeBoard : IDisposable
    {
        private readonly BlockingCollection<byte> _toBoard = [];
        private readonly BlockingCollection<byte> _toPi = [];
        private readonly Thread _thread;
        private volatile bool _stop;
        public bool Silent;
        public readonly Dictionary<int, bool> Levels = new() { [5] = false, [6] = true };   // physical levels: safe
        public bool SwitchClosed;                     // input 12, active-low: closed pulls the line down
        public int Hellos, Writes, Reads;

        public FakeBoard()
        {
            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();
        }

        public Stream PiSide => new QueueStream(_toPi, _toBoard);

        private void Serve()
        {
            var decoder = new LinkFrameDecoder();
            while (!_stop)
            {
                if (!_toBoard.TryTake(out var b, 100)) continue;
                if (decoder.Feed(b) is not { } frame || frame.Type != LinkFrame.Request) continue;
                if (Silent) continue;
                LinkFrame.TryParseRequest(frame.Payload, out var path, out var body);
                var (status, resp) = Answer(path, Encoding.UTF8.GetString(body));
                foreach (var o in LinkFrame.Encode(LinkFrame.Response, frame.Seq, LinkFrame.ResponsePayload(status, Encoding.UTF8.GetBytes(resp)))) _toPi.Add(o);
            }
        }

        private (int, string) Answer(string path, string body)
        {
            switch (path)
            {
                case LinkPortsClient.HelloPath:
                    Hellos++;
                    return (200, "{\"profile\":\"bench\",\"firmware\":\"bench-1\",\"watchdog_s\":3,\"tripped\":false," +
                                 "\"pins\":[{\"pin\":5,\"active_high\":true,\"max_on_s\":30,\"level\":false},{\"pin\":6,\"active_high\":false,\"max_on_s\":0,\"level\":false}]," +
                                 $"\"inputs\":[{{\"pin\":12,\"active_high\":false,\"level\":{(SwitchClosed ? "true" : "false")}}}],\"channels\":[0]}}");
                case LinkPortsClient.WritePath:
                    Writes++;
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var pin = doc.RootElement.GetProperty("pin").GetInt32();
                        var level = doc.RootElement.GetProperty("level").GetBoolean();
                        if (!Levels.ContainsKey(pin)) return (404, $"{{\"error\":\"pin {pin} is not a port of this board\"}}");
                        Levels[pin] = pin == 5 ? level : !level;   // pin 6 is active-low: active = physical low
                        return (200, LinkPortsClient.WriteBody(pin, level));
                    }
                case LinkPortsClient.ReadPinPath:
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var pin = doc.RootElement.GetProperty("pin").GetInt32();
                        if (pin != 12) return (404, $"{{\"error\":\"pin {pin} is not an input of this board\"}}");
                        return (200, $"{{\"pin\":12,\"level\":{(SwitchClosed ? "true" : "false")}}}");
                    }
                case LinkPortsClient.ReadPath:
                    Reads++;
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var channel = doc.RootElement.GetProperty("channel").GetInt32();
                        return channel == 0 ? (200, "{\"channel\":0,\"volts\":0.75}") : (404, $"{{\"error\":\"channel {channel} is not a port of this board\"}}");
                    }
                default:
                    return (404, "{\"error\":\"not a port request\"}");
            }
        }

        public void Dispose() { _stop = true; _thread.Join(1000); }
    }

    private sealed class FakeProvider(FakeBoard board) : ILinkPortsProvider
    {
        private LinkPortsClient? _client;
        public LinkPortsClient Open(string link) => _client ??= new LinkPortsClient(board.PiSide, timeout: TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_client_says_hello_writes_logical_levels_and_reads_volts()
    {
        using var board = new FakeBoard();
        using var client = new LinkPortsClient(board.PiSide, timeout: TimeSpan.FromSeconds(2));

        var hello = client.Hello();
        Assert.Equal("bench", hello.Profile);
        Assert.Equal(2, hello.Pins.Count);

        client.Write(5, true);
        Assert.True(board.Levels[5]);
        client.Write(5, false);
        Assert.False(board.Levels[5]);
        Assert.Equal(0.75, client.Read(0));

        var refused = Assert.Throws<LinkPortsException>(() => client.Write(9, true));
        Assert.Equal(404, refused.Status);
        Assert.Contains("pin 9", refused.Error);
        Assert.Throws<LinkPortsException>(() => client.Read(3));

        // the switch: asserted or not, never a level the board did not give
        Assert.False(client.ReadPin(12));
        board.SwitchClosed = true;
        Assert.True(client.ReadPin(12));
        var noSuchInput = Assert.Throws<LinkPortsException>(() => client.ReadPin(5));
        Assert.Equal(404, noSuchInput.Status);
        Assert.Equal(9, client.Requests);
    }

    [Fact]
    public void A_linked_input_reads_the_switch_through_the_boards_polarity_and_refuses_a_mismatch()
    {
        using var board = new FakeBoard();
        var provider = new FakeProvider(board);

        // input 12 is active-LOW on the board: closed pulls the line down, so a closed switch is physically false
        var line = LinkSettings.OpenInputLine(provider, "board", 12, activeHigh: false);
        Assert.True(line.Read());               // open: the pull-up holds it high
        board.SwitchClosed = true;
        Assert.False(line.Read());              // closed: pulled down

        var mismatch = Assert.Throws<ArgumentException>(() => LinkSettings.OpenInputLine(provider, "board", 12, activeHigh: true));
        Assert.Contains("active-low", mismatch.Message);
        Assert.Throws<ArgumentException>(() => LinkSettings.OpenInputLine(provider, "board", 99, activeHigh: false));
        Assert.Throws<ArgumentException>(() => LinkSettings.OpenInputLine(provider, "board", 5, activeHigh: true));   // an output is not an input

        // and the whole way through the driver: a sensor whose reading is the switch, 1 when asserted
        var sensors = new GpioChardevSensorFactory(links: provider);
        var sensor = sensors.Create();
        var configured = sensor.Configure(new Dictionary<string, string>
        {
            ["capability"] = "sense.valve_closed", ["link"] = "/dev/ttyUSB0", ["pin"] = "12", ["active_high"] = "false"
        });
        Assert.True(configured.IsValid, string.Join("; ", configured.Errors));
        var executor = sensor.Executors.Single();
        var outcome = executor.Execute(new CapabilityExecution
        {
            CapabilityId = "sense.valve_closed",
            Parameters = new Dictionary<string, double>(),
            StartedAt = DateTimeOffset.Parse("2026-09-06T12:00:00Z"),
            EffectiveLimits = new CapabilityLimits()
        });
        Assert.True(outcome.Succeeded);
        Assert.True(EvidenceReadings.TryRead(outcome.Evidence.Single(), out var value));
        Assert.Equal(1, value);                 // the switch is closed, and that is what the mound records
    }

    [Fact]
    public void A_linked_output_drives_physical_levels_through_the_boards_polarity_and_comes_up_safe()
    {
        using var board = new FakeBoard();
        var provider = new FakeProvider(board);

        // pin 6 is active-low on the board: the manifest must say so, and then a physical LOW is the active level
        var lowSide = LinkSettings.OpenOutput(provider, "board", 6, activeHigh: false);
        Assert.True(lowSide.State);                  // brought up at its safe level, which for active-low is physical HIGH
        Assert.True(board.Levels[6]);
        lowSide.Write(false);                        // physical low = active
        Assert.False(board.Levels[6]);
        lowSide.Write(true);                         // physical high = safe
        Assert.True(board.Levels[6]);

        var highSide = LinkSettings.OpenOutput(provider, "board", 5, activeHigh: true);
        Assert.False(board.Levels[5]);
        highSide.Write(true);
        Assert.True(board.Levels[5]);

        // a polarity the board disagrees with is refused at bring-up, never driven
        var mismatch = Assert.Throws<ArgumentException>(() => LinkSettings.OpenOutput(provider, "board", 5, activeHigh: false));
        Assert.Contains("active-high", mismatch.Message);
        Assert.Throws<ArgumentException>(() => LinkSettings.OpenOutput(provider, "board", 7, activeHigh: true));
        Assert.Throws<ArgumentException>(() => LinkSettings.OpenInput(provider, "board", 2));
        Assert.Equal(0.75, LinkSettings.OpenInput(provider, "board", 0).Read());
    }

    [Fact]
    public void A_silent_board_is_a_timeout_not_a_hang_and_a_keepalive_keeps_saying_hello()
    {
        using var board = new FakeBoard();
        using var client = new LinkPortsClient(board.PiSide, timeout: TimeSpan.FromMilliseconds(600));
        board.Silent = true;
        Assert.Throws<TimeoutException>(() => client.Read(0));
        Assert.Equal(1, client.Timeouts);
        board.Silent = false;
        Assert.Equal(0.75, client.Read(0));          // the late nothing did not confuse the next exchange

        client.StartKeepalive(TimeSpan.FromMilliseconds(150));
        Thread.Sleep(700);
        Assert.True(board.Hellos >= 2, $"{board.Hellos} hello(s)");
    }

    [Fact]
    public void The_hardware_factories_compose_a_line_and_a_channel_on_the_board_by_the_link_setting_alone()
    {
        using var board = new FakeBoard();
        var provider = new FakeProvider(board);

        var actuators = new GpioChardevActuatorFactory(links: provider);
        var actuator = actuators.Create();
        var configured = actuator.Configure(new Dictionary<string, string>
        {
            ["capability"] = "act.relay_1", ["link"] = "/dev/ttyUSB0", ["pin"] = "5", ["max_on_s"] = "10"
        });
        Assert.True(configured.IsValid, string.Join("; ", configured.Errors));
        Assert.False(board.Levels[5]);               // the line came up safe through the board

        var sensors = new Ads1115AnalogSensorFactory(links: provider);
        var sensor = sensors.Create();
        var sensorConfigured = sensor.Configure(new Dictionary<string, string>
        {
            ["capability"] = "sense.temp", ["link"] = "/dev/ttyUSB0", ["channel"] = "0", ["scale"] = "100", ["offset"] = "-50", ["unit"] = "C"
        });
        Assert.True(sensorConfigured.IsValid, string.Join("; ", sensorConfigured.Errors));

        // a mismatched polarity refuses the manifest fail-closed, through the factory
        var wrong = actuators.Create().Configure(new Dictionary<string, string>
        {
            ["capability"] = "act.relay_1", ["link"] = "/dev/ttyUSB0", ["pin"] = "5", ["active_high"] = "false"
        });
        Assert.False(wrong.IsValid);
        Assert.Contains(wrong.Errors, e => e.Contains("active-high"));
    }
}
