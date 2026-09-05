using System.Text.Encodings.Web;

namespace Micromound.Protocol;

/// <summary>
/// The string escaping of the canonical wire bytes, stated as a rule small enough to hold in one head
/// and implement in fifty lines of C, with no Unicode tables — PROTOCOL.md §2 "Encoding rules".
///
/// <para><b>Why this exists.</b> The relaxed encoder used before (<c>UnsafeRelaxedJsonEscaping</c>)
/// leaves most non-ASCII characters literal but escapes a long, runtime-specific set: unassigned code
/// points, separators, private use — 7,886 of them under one .NET version, a different count under the
/// next as Unicode tables move. Two mounds running different runtimes would sign DIFFERENT canonical
/// bytes for the same string, and no C encoder could mirror a table it cannot see. So the canonical
/// form is ASCII-only:</para>
///
/// <list type="bullet">
///   <item><c>"</c> → <c>\"</c>, <c>\</c> → <c>\\</c></item>
///   <item>U+0008, U+0009, U+000A, U+000C, U+000D → <c>\b \t \n \f \r</c></item>
///   <item>every other code point below U+0020, and every code point from U+007F up, → <c>\uXXXX</c>
///   with four UPPERCASE hex digits; code points above U+FFFF as a UTF-16 surrogate pair
///   (<c>😀</c>)</item>
///   <item>everything else — printable ASCII including <c>+ &lt; &gt; &amp; ' /</c> — literal</item>
/// </list>
///
/// <para>The golden fixtures contain only ASCII, so their bytes do not change. Strings that carry
/// non-ASCII (a device name, a mission's context) now canonicalize identically everywhere, and
/// <c>tests/Micromound.Tests/Golden/files/canonical-strings.txt</c> pins the rule for the C mirror.</para>
/// </summary>
public sealed class CanonicalJsonEncoder : JavaScriptEncoder
{
    public static readonly CanonicalJsonEncoder Instance = new();

    private CanonicalJsonEncoder() { }

    /// <summary>A surrogate pair is 12 characters: <c>\uXXXX\uXXXX</c>.</summary>
    public override int MaxOutputCharactersPerInputCharacter => 12;

    /// <summary>True for every scalar the canonical form escapes: quote, backslash, controls, non-ASCII.</summary>
    public override bool WillEncode(int unicodeScalar) =>
        unicodeScalar < 0x20 || unicodeScalar >= 0x7F || unicodeScalar == '"' || unicodeScalar == '\\';

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (var i = 0; i < textLength; i++)
        {
            var c = text[i];
            if (c < 0x20 || c >= 0x7F || c == '"' || c == '\\')
                return i;
        }
        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        numberOfCharactersWritten = 0;
        switch (unicodeScalar)
        {
            case '"': return Write(buffer, bufferLength, "\\\"", out numberOfCharactersWritten);
            case '\\': return Write(buffer, bufferLength, "\\\\", out numberOfCharactersWritten);
            case '\b': return Write(buffer, bufferLength, "\\b", out numberOfCharactersWritten);
            case '\t': return Write(buffer, bufferLength, "\\t", out numberOfCharactersWritten);
            case '\n': return Write(buffer, bufferLength, "\\n", out numberOfCharactersWritten);
            case '\f': return Write(buffer, bufferLength, "\\f", out numberOfCharactersWritten);
            case '\r': return Write(buffer, bufferLength, "\\r", out numberOfCharactersWritten);
        }

        if (unicodeScalar <= 0xFFFF)
            return WriteUnit(buffer, bufferLength, (ushort)unicodeScalar, out numberOfCharactersWritten);

        // Above the BMP: the UTF-16 surrogate pair, each half as \uXXXX.
        var v = unicodeScalar - 0x10000;
        var high = (ushort)(0xD800 + (v >> 10));
        var low = (ushort)(0xDC00 + (v & 0x3FF));
        if (bufferLength < 12) return false;
        WriteUnit(buffer, 6, high, out var a);
        WriteUnit(buffer + 6, 6, low, out var b);
        numberOfCharactersWritten = a + b;
        return true;
    }

    private static unsafe bool Write(char* buffer, int bufferLength, string s, out int written)
    {
        written = 0;
        if (bufferLength < s.Length) return false;
        for (var i = 0; i < s.Length; i++) buffer[i] = s[i];
        written = s.Length;
        return true;
    }

    private static unsafe bool WriteUnit(char* buffer, int bufferLength, ushort unit, out int written)
    {
        written = 0;
        if (bufferLength < 6) return false;
        const string hex = "0123456789ABCDEF";
        buffer[0] = '\\';
        buffer[1] = 'u';
        buffer[2] = hex[(unit >> 12) & 0xF];
        buffer[3] = hex[(unit >> 8) & 0xF];
        buffer[4] = hex[(unit >> 4) & 0xF];
        buffer[5] = hex[unit & 0xF];
        written = 6;
        return true;
    }
}
