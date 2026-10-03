using System.Text;

namespace FilesystemMcp;

internal enum TextClass { Text, Binary, UnsupportedEncoding }

internal readonly record struct TextClassResult(TextClass Class, TextDocument? Document);

// Minimal FS-03 representation: strict decoding plus a normalized-offset map.
// FS-09 (1.10.0) shares this decode policy through FileContentReader's streaming
// scanner (SearchLinesAsync over the same ScanOnceAsync read_file uses), not through
// this whole-payload parser, which stays on the read/patch path that needs the map.
internal sealed class TextDocument
{
    private const int ParseChunkBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16Le = new(false, true, true);
    private static readonly UnicodeEncoding Utf16Be = new(true, true, true);
    private static readonly UTF32Encoding Utf32Le = new(false, true, true);
    private static readonly UTF32Encoding Utf32Be = new(true, true, true);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];
    private static readonly byte[] Utf32LeBom = [0xFF, 0xFE, 0x00, 0x00];
    private static readonly byte[] Utf32BeBom = [0x00, 0x00, 0xFE, 0xFF];

    internal string Text { get; }
    internal string Canonical { get; }
    private readonly Encoding _encoding;
    private readonly byte[] _bom;
    private readonly int[] _offsets;

    private TextDocument(string text, Encoding encoding, byte[] bom)
    {
        Text = text; _encoding = encoding; _bom = bom;
        var canonical = new StringBuilder(text.Length);
        var offsets = new List<int>(text.Length + 1);
        for (var i = 0; i < text.Length; i++)
        {
            offsets.Add(i);
            if (text[i] == '\r')
            {
                canonical.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else canonical.Append(text[i]);
        }
        offsets.Add(text.Length);
        Canonical = canonical.ToString(); _offsets = offsets.ToArray();
    }

    internal static TextDocument Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var (encoding, bomLength) = Signature(bytes);
        return DecodePayload(encoding, bomLength == 0 ? [] : bytes[..bomLength], bytes.AsSpan(bomLength));
    }

    // Short reads are concatenated before the signature is chosen, so a BOM split
    // across 1–3 byte chunks is still recognized. The strict decoder then runs on
    // the whole payload; an incomplete trailing sequence fails closed.
    internal static async Task<TextDocument> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        using var pending = new MemoryStream();
        var buffer = new byte[ParseChunkBytes];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            pending.Write(buffer, 0, read);
        }
        return Decode(pending.ToArray());
    }

    // Binary policy is separate from strict decoding. Do not treat a raw NUL scan as
    // a substitute for decoding a BOM encoding: valid UTF-16/UTF-32 text contains NUL
    // bytes. A decoded U+0000 is binary_file only for UTF-8 without a BOM. Invalid
    // sequences are unsupported_encoding and are never replaced with U+FFFD.
    internal static TextClassResult Classify(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try { return new(TextClass.Text, Decode(bytes)); }
        catch (MutationException ex) when (ex.Code == "binary_file") { return new(TextClass.Binary, null); }
        catch (MutationException ex) when (ex.Code == "unsupported_encoding") { return new(TextClass.UnsupportedEncoding, null); }
    }

    internal static async Task<TextClassResult> ClassifyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try { return new(TextClass.Text, await ParseAsync(stream, cancellationToken)); }
        catch (MutationException ex) when (ex.Code == "binary_file") { return new(TextClass.Binary, null); }
        catch (MutationException ex) when (ex.Code == "unsupported_encoding") { return new(TextClass.UnsupportedEncoding, null); }
    }

    // FS-11 (1.11.0): the canonical offset of the first exact match travels back with the
    // result. It is the only reliable anchor for the response snippet: searching the result
    // for the replacement text is wrong for an empty replacement and ambiguous when the
    // replacement text already occurred earlier in the file.
    internal (string Canonical, byte[] Bytes, int Index) Replace(string target, string replacement)
    {
        target = FileTextHelper.NormalizeLineEndings(target);
        replacement = FileTextHelper.NormalizeLineEndings(replacement);
        var index = Canonical.IndexOf(target, StringComparison.Ordinal);
        if (index < 0) throw new MutationException("target_not_found", "Target snippet not found.");
        var start = _offsets[index]; var end = _offsets[index + target.Length];
        var style = LineStyle(Text[start..end]) ?? LineStyle(Text) ?? "\n";
        var stored = Text[..start] + replacement.Replace("\n", style, StringComparison.Ordinal) + Text[end..];
        var encoded = _encoding.GetBytes(stored); // Encoder completion precedes any temp creation.
        var result = new byte[_bom.Length + encoded.Length];
        _bom.CopyTo(result, 0); encoded.CopyTo(result, _bom.Length);
        return (Decode(result).Canonical, result, index);
    }

    private static TextDocument DecodePayload(Encoding encoding, byte[] bom, ReadOnlySpan<byte> payload)
    {
        try
        {
            var text = encoding.GetString(payload);
            if (bom.Length == 0 && text.Contains('\0')) throw new MutationException("binary_file", "Binary file cannot be mutated as text.");
            return new TextDocument(text, encoding, bom);
        }
        catch (DecoderFallbackException) { throw new MutationException("unsupported_encoding", "Invalid or unsupported text encoding."); }
    }

    private static (Encoding Encoding, int BomLength) Signature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes.StartsWith(Utf32LeBom)) return (Utf32Le, 4);
        if (bytes.Length >= 4 && bytes.StartsWith(Utf32BeBom)) return (Utf32Be, 4);
        if (bytes.Length >= 2 && bytes.StartsWith(Utf16LeBom)) return (Utf16Le, 2);
        if (bytes.Length >= 2 && bytes.StartsWith(Utf16BeBom)) return (Utf16Be, 2);
        if (bytes.Length >= 3 && bytes.StartsWith(Utf8Bom)) return (Utf8, 3);
        return (Utf8, 0);
    }

    private static string? LineStyle(string text)
    {
        var crlf = 0; var lf = 0; var cr = 0;
        var crlfAt = int.MaxValue; var lfAt = int.MaxValue; var crAt = int.MaxValue;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                if (crlf++ == 0) crlfAt = i;
                i++;
            }
            else if (text[i] == '\n') { if (lf++ == 0) lfAt = i; }
            else if (text[i] == '\r') { if (cr++ == 0) crAt = i; }
        }

        string? best = null;
        var bestCount = 0;
        var bestIndex = int.MaxValue;
        Consider("\r\n", crlf, crlfAt);
        Consider("\n", lf, lfAt);
        Consider("\r", cr, crAt);
        return best;

        void Consider(string style, int count, int index)
        {
            if (count > bestCount || (count == bestCount && count > 0 && index < bestIndex))
            {
                best = style; bestCount = count; bestIndex = index;
            }
        }
    }
}
