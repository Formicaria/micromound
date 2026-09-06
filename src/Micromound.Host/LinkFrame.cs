using System.Buffers.Binary;
using System.Text;

namespace Micromound.Host;

/// <summary>
/// The Pi↔ESP32 link framing — PROTOCOL.md §12, mirrored byte for byte by
/// <c>firmware/micromound-c/mm_frame</c>. A frame is
/// <c>"MM" ver(1) type(1) seq(1) len(2, LE) payload(len) crc32(4, LE)</c>, the CRC the IEEE 802.3
/// (zlib) CRC-32 over everything before it. A request payload is <c>path '\n' body</c>; a response
/// payload is <c>status '\n' body</c>, status 0 meaning the bridge could not exchange at all.
/// The payload is opaque here: the JSON inside is the signed protocol, and a bridge that altered a
/// byte of it would hand the controller an envelope that no longer verifies.
/// </summary>
public static class LinkFrame
{
    public const byte Version = 1;
    public const byte Request = 0x01;
    public const byte Response = 0x02;
    public const int HeaderLength = 7;
    public const int TrailerLength = 4;
    public const int MaxPayload = 8192;

    /// <summary>The one request a bridge answers itself: the Pi's clock, as <c>{"epoch_s":N}</c>.</summary>
    public const string TimePath = "micromound/link/time";

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>IEEE 802.3 CRC-32 (reflected 0xEDB88320, init and final xor 0xFFFFFFFF) — zlib's crc32.</summary>
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    public static byte[] Encode(byte type, byte seq, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(payload), $"payload of {payload.Length} bytes exceeds the link's {MaxPayload}");
        var frame = new byte[HeaderLength + payload.Length + TrailerLength];
        frame[0] = (byte)'M'; frame[1] = (byte)'M';
        frame[2] = Version;
        frame[3] = type;
        frame[4] = seq;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5, 2), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(HeaderLength + payload.Length, 4), Crc32(frame.AsSpan(0, HeaderLength + payload.Length)));
        return frame;
    }

    public static byte[] RequestPayload(string path, ReadOnlySpan<byte> body)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\n')) throw new ArgumentException("a link path is one non-empty line", nameof(path));
        var head = Encoding.ASCII.GetBytes(path + "\n");
        var payload = new byte[head.Length + body.Length];
        head.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(head.Length));
        return payload;
    }

    public static byte[] ResponsePayload(int status, ReadOnlySpan<byte> body)
    {
        if (status is < 0 or > 999) throw new ArgumentOutOfRangeException(nameof(status));
        var head = Encoding.ASCII.GetBytes($"{status}\n");
        var payload = new byte[head.Length + body.Length];
        head.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(head.Length));
        return payload;
    }

    /// <summary>Splits a request payload into its path and body. False when there is no path line.</summary>
    public static bool TryParseRequest(ReadOnlySpan<byte> payload, out string path, out ReadOnlySpan<byte> body)
    {
        path = ""; body = default;
        var nl = payload.IndexOf((byte)'\n');
        if (nl <= 0) return false;
        path = Encoding.ASCII.GetString(payload[..nl]);
        body = payload[(nl + 1)..];
        return true;
    }

    /// <summary>Splits a response payload into its status and body. False when there is no decimal status line.</summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> payload, out int status, out ReadOnlySpan<byte> body)
    {
        status = 0; body = default;
        var nl = payload.IndexOf((byte)'\n');
        if (nl is <= 0 or > 3) return false;
        var value = 0;
        foreach (var c in payload[..nl])
        {
            if (c is < (byte)'0' or > (byte)'9') return false;
            value = value * 10 + (c - '0');
        }
        status = value;
        body = payload[(nl + 1)..];
        return true;
    }
}

/// <summary>One decoded frame.</summary>
public readonly record struct LinkFrameData(byte Type, byte Seq, byte[] Payload);

/// <summary>
/// The incremental decoder: feed it bytes as they arrive; it resynchronises on the magic and drops —
/// and counts — anything with the wrong version, an oversize length, or a bad CRC. The same rules as
/// the C decoder, so a bridge and a board disagree about no byte stream.
/// </summary>
public sealed class LinkFrameDecoder
{
    private readonly byte[] _buf = new byte[LinkFrame.HeaderLength + LinkFrame.MaxPayload + LinkFrame.TrailerLength];
    private int _n;
    private int _expect;

    public int Frames { get; private set; }
    public int DroppedCrc { get; private set; }
    public int DroppedVersion { get; private set; }
    public int DroppedOversize { get; private set; }
    public int Resyncs { get; private set; }

    /// <summary>Feeds one byte. Returns the completed frame, or null.</summary>
    public LinkFrameData? Feed(byte b)
    {
        if (_n == 0) { if (b == 'M') _buf[_n++] = b; return null; }
        if (_n == 1) { if (b == 'M') _buf[_n++] = b; else _n = 0; return null; }
        if (_n >= _buf.Length) Resync();
        _buf[_n++] = b;

        while (true)
        {
            if (_n < 2) return null;
            if (_expect == 0 && _n >= LinkFrame.HeaderLength)
            {
                var len = _buf[5] | (_buf[6] << 8);
                if (_buf[2] != LinkFrame.Version) { DroppedVersion++; Resync(); continue; }
                if (len > LinkFrame.MaxPayload) { DroppedOversize++; Resync(); continue; }
                _expect = LinkFrame.HeaderLength + len + LinkFrame.TrailerLength;
            }
            if (_expect > 0 && _n >= _expect)
            {
                var body = _expect - LinkFrame.TrailerLength;
                var want = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(body, 4));
                if (LinkFrame.Crc32(_buf.AsSpan(0, body)) != want) { DroppedCrc++; Resync(); continue; }
                var frame = new LinkFrameData(_buf[3], _buf[4], _buf.AsSpan(LinkFrame.HeaderLength, body - LinkFrame.HeaderLength).ToArray());
                Frames++;
                _n = 0; _expect = 0;
                return frame;
            }
            return null;
        }
    }

    private void Resync()
    {
        Resyncs++;
        var keep = 0;
        for (var i = 1; i < _n; i++)
            if (_buf[i] == 'M' && (i + 1 == _n || _buf[i + 1] == 'M')) { keep = _n - i; break; }
        if (keep > 0) Array.Copy(_buf, _n - keep, _buf, 0, keep);
        _n = keep;
        _expect = 0;
    }
}
