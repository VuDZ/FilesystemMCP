using System.Text;

namespace FilesystemMcp;

// Minimal FS-03 prerequisite: strict format-preserving mutation representation.
internal sealed class TextDocument
{
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
        Encoding encoding = new UTF8Encoding(false, true);
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 255, 254, 0, 0 })) { encoding = new UTF32Encoding(false, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 254, 255 })) { encoding = new UTF32Encoding(true, true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 255, 254 })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 254, 255 })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) skip = 3;
        try
        {
            var text = encoding.GetString(bytes, skip, bytes.Length - skip);
            if (skip == 0 && text.Contains('\0')) throw new MutationException("binary_file", "Binary file cannot be mutated as text.");
            return new(text, encoding, bytes[..skip]);
        }
        catch (DecoderFallbackException) { throw new MutationException("unsupported_encoding", "Invalid or unsupported text encoding."); }
    }

    internal (string Canonical, byte[] Bytes) Replace(string target, string replacement)
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
        return (Decode(result).Canonical, result);
    }

    private static string? LineStyle(string text)
    {
        var styles = new Dictionary<string, int>();
        for (var i = 0; i < text.Length; i++)
        {
            string? style = null;
            if (text[i] == '\r') { style = "\r"; if (i + 1 < text.Length && text[i + 1] == '\n') { style = "\r\n"; i++; } }
            else if (text[i] == '\n') style = "\n";
            if (style is not null) styles[style] = styles.GetValueOrDefault(style) + 1;
        }
        return styles.OrderByDescending(item => item.Value).Select(item => item.Key).FirstOrDefault();
    }
}
