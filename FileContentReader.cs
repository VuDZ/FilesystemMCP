using System.Security.Cryptography;
using System.Text;

namespace FilesystemMcp;

/// <summary>
/// One bounded canonical read of a text file: the selected canonical text, the MD5 and
/// SHA-256 of the <em>whole</em> canonical file, and the number of lines the whole file
/// contains. The hashes always describe the complete normalized file, even when only a
/// line range was materialized. FS-10 adds the selection metadata: the actually returned
/// 1-based range (<see cref="StartLine"/>/<see cref="EndLine"/>, null for an empty
/// selection), whether a line cap truncated the answer and whether lines exist past the
/// returned <see cref="EndLine"/>.
/// </summary>
internal sealed record CanonicalContent(
    string Text,
    string Md5,
    string Sha256,
    int TotalLines,
    int? StartLine = null,
    int? EndLine = null,
    bool Truncated = false,
    bool HasMore = false);

/// <summary>
/// FS-07 bounded, streaming reader for text content. It replaces the previous
/// <c>ReadToEnd</c> + whole-file <c>byte[]</c> pipeline with a single pass: bytes are
/// decoded strictly in chunks with the FS-03 encoding policy, line endings are
/// canonicalized while the characters stream through, and the content hash is fed
/// chunk by chunk into incremental digests.
/// </summary>
/// <remarks>
/// <para>
/// Memory depends on the chunk size, the requested range and
/// <see cref="ResourceBudget.MaxLineChars"/> — never on the file length. Two budgets are
/// enforced here: <see cref="ResourceBudget.MaxFileBytes"/> before anything can be
/// materialized (the content hash covers the whole file, so a one-line range read does
/// not bypass it) and <see cref="ResourceBudget.MaxLineChars"/> while lines are split,
/// so an oversized line is refused before it becomes a string.
/// </para>
/// <para>
/// Classification order matches <see cref="TextDocument"/> exactly: the byte budget is
/// checked first, then strict decoding (an invalid sequence anywhere is
/// <c>unsupported_encoding</c>), then the binary verdict (a decoded U+0000 is
/// <c>binary_file</c> only for UTF-8 without a BOM, and only after the whole payload
/// decoded cleanly, so a file that also has an invalid sequence still reports the
/// encoding failure). The line budget is an FS-07 addition and is checked while lines
/// are split.
/// </para>
/// </remarks>
internal static class FileContentReader
{
    /// <summary>Read granularity. Matches <see cref="TextDocument"/>'s parse chunk.</summary>
    private const int ChunkBytes = 4096;

    // The same strict encodings TextDocument uses. throwOnInvalidBytes makes every
    // fallback an exception instead of a silent U+FFFD replacement.
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

    /// <summary>
    /// Strict decode + canonical EOL, streaming content hash, byte and line budgets.
    /// </summary>
    /// <param name="resolvedPath">An already policy-resolved path.</param>
    /// <param name="startLine">First selected line, 1-based, inclusive. Null with a null <paramref name="endLine"/> means "all lines".</param>
    /// <param name="endLine">Last selected line, 1-based, inclusive.</param>
    /// <param name="captureFullText">
    /// True for the full-file read path: <see cref="CanonicalContent.Text"/> is the whole
    /// canonical text (bounded by <paramref name="maxLines"/> when supplied). False
    /// materializes only the requested range, while the hashes and
    /// <see cref="CanonicalContent.TotalLines"/> still cover the whole file.
    /// </param>
    /// <param name="budget">The immutable FS-07 budget set.</param>
    /// <param name="cancellationToken">Observed on every read and inside the line loop.</param>
    /// <param name="openStream">
    /// Deterministic test seam: reads through this factory instead of opening the path.
    /// Null in production.
    /// </param>
    /// <param name="beforeRetry">Deterministic retry-observation seam; null in production.</param>
    /// <param name="maxLines">
    /// Materialization prefix cap for the full-file path, mirroring
    /// <see cref="FileTextHelper.ExtractRequestedContent"/>'s truncation. Null means no
    /// cap. The scan itself always covers the whole file.
    /// </param>
    /// <param name="textLimit">
    /// Hard bound, in UTF-16 code units, on the materialized selected text. Null means the
    /// caller has already bounded the selection by line and byte budgets. Passing the
    /// response budget here is what keeps a full-file read from building a payload that
    /// the transport is guaranteed to replace: the refusal happens during the scan.
    /// </param>
    public static async Task<CanonicalContent> ReadCanonicalAsync(
        string resolvedPath,
        int? startLine,
        int? endLine,
        bool captureFullText,
        ResourceBudget budget,
        CancellationToken cancellationToken = default,
        Func<string, Stream>? openStream = null,
        Action<int>? beforeRetry = null,
        int? maxLines = null,
        int? textLimit = null)
    {
        ArgumentNullException.ThrowIfNull(resolvedPath);
        ArgumentNullException.ThrowIfNull(budget);

        var result = await ScanAsync(
            resolvedPath,
            budget,
            () => new CanonicalSink(budget, startLine, endLine, captureFullText, maxLines, textLimit),
            hashContent: true,
            openStream,
            beforeRetry,
            cancellationToken).ConfigureAwait(false);

        var sink = (CanonicalSink)result.Sink;
        return new CanonicalContent(
            sink.SelectedText,
            result.Digests.Md5,
            result.Digests.Sha256,
            sink.TotalLines,
            sink.ActualStartLine,
            sink.ActualEndLine,
            sink.Truncated,
            sink.HasMore);
    }

    /// <summary>
    /// Every line of the file, canonical EOL, no line-count cap. Each line is still
    /// capped by <see cref="ResourceBudget.MaxLineChars"/>. Used by list_directory-style
    /// callers.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadAllLinesAsync(
        string resolvedPath,
        ResourceBudget budget,
        CancellationToken cancellationToken = default,
        Func<string, Stream>? openStream = null,
        Action<int>? beforeRetry = null)
    {
        ArgumentNullException.ThrowIfNull(resolvedPath);
        ArgumentNullException.ThrowIfNull(budget);

        var result = await ScanAsync(
            resolvedPath,
            budget,
            () => new AllLinesSink(budget),
            hashContent: false,
            openStream,
            beforeRetry,
            cancellationToken).ConfigureAwait(false);

        return ((AllLinesSink)result.Sink).Lines;
    }

    /// <summary>
    /// FS-09: the search tool's line stream. It is the very scanner the read path uses —
    /// the same byte budget, the same BOM precedence, the same strict decode and the same
    /// deferred binary verdict — minus the read-only concerns: no sharing retry (search
    /// reports a locked file as an immediate per-file skip, not as a backoff that would
    /// spend the client's deadline) and no content digests. Every decoded line is handed
    /// to <paramref name="emit"/> with its 1-based number, so a search never materializes
    /// more than one line regardless of the file size. The whole file is always scanned:
    /// the binary and encoding verdicts are only final at end-of-stream, so a NUL or an
    /// invalid sequence after any number of matches still reclassifies the file.
    /// </summary>
    /// <param name="afterHeadProbe">
    /// Deterministic test seam, invoked after the signature probe and before the decode
    /// while the single read stream is still open; null in production.
    /// </param>
    /// <param name="openStream">
    /// Deterministic test seam: reads through this factory instead of opening the path.
    /// Null in production.
    /// </param>
    internal static Task SearchLinesAsync(
        string resolvedPath,
        ResourceBudget budget,
        Action<int, string> emit,
        Action? afterHeadProbe,
        Func<string, Stream>? openStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolvedPath);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(emit);

        return ScanOnceAsync(
            resolvedPath,
            budget,
            () => new SearchLineSink(budget, emit),
            hashContent: false,
            openStream,
            afterHeadProbe,
            cancellationToken);
    }

    /// <summary>
    /// One retried scan. The sink is created per attempt, so a retried read never mixes
    /// text from two attempts.
    /// </summary>
    private static async Task<ScanResult> ScanAsync(
        string resolvedPath,
        ResourceBudget budget,
        Func<LineSink> sinkFactory,
        bool hashContent,
        Func<string, Stream>? openStream,
        Action<int>? beforeRetry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await SharingRetry.RunAsync(
                token => ScanOnceAsync(resolvedPath, budget, sinkFactory, hashContent, openStream, afterHeadProbe: null, token),
                beforeRetry,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            // A strict decoder failure is the FS-03 unsupported_encoding outcome, exactly
            // as TextDocument.DecodePayload reports it: an invalid sequence is never
            // replaced with U+FFFD and never escapes as a raw decoder exception.
            if (ex is DecoderFallbackException)
            {
                throw new MutationException("unsupported_encoding", "Invalid or unsupported text encoding.");
            }

            var translated = FileErrorClassifier.Translate(ex);
            if (ReferenceEquals(translated, ex))
            {
                throw;
            }

            throw translated;
        }
    }

    private static async Task<ScanResult> ScanOnceAsync(
        string resolvedPath,
        ResourceBudget budget,
        Func<LineSink> sinkFactory,
        bool hashContent,
        Func<string, Stream>? openStream,
        Action? afterHeadProbe,
        CancellationToken cancellationToken)
    {
        var sink = sinkFactory();
        var processor = new ChunkProcessor(sink, hashContent);
        var stream = openStream is null ? FileTextHelper.OpenReadStream(resolvedPath) : openStream(resolvedPath);
        await using (stream.ConfigureAwait(false))
        {
            // A stream that reports its length is checked before a single byte is read;
            // the counted check below covers streams that only reveal their size by
            // being read.
            ResourceBudgets.EnsureStreamWithinBudget(stream, budget);

            // Up to four bytes of signature, gathered across as many reads as the stream
            // needs, so a BOM split into one-byte reads is still recognized.
            var head = new byte[4];
            var headCount = 0;
            long totalBytes = 0;
            while (headCount < head.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(head.AsMemory(headCount), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                headCount += read;
                totalBytes += read;
                EnsureCountedBytesWithinBudget(totalBytes, budget);
            }

            // The probe is done: the signature bytes are in hand and the single stream is
            // still open. The FS-09 search path hooks its fault-injection seam in here,
            // between the probe and the decode, exactly where a mid-read delete happens.
            afterHeadProbe?.Invoke();

            var (encoding, bomLength) = Signature(head, headCount);
            processor.Start(encoding, nulIsBinary: bomLength == 0);
            if (headCount > bomLength)
            {
                processor.Process(head, bomLength, headCount - bomLength, cancellationToken);
            }

            var buffer = new byte[ChunkBytes];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                // A stream (or a cancellation hook it calls) may have cancelled the token
                // while handing out data: observe it before doing any more work.
                cancellationToken.ThrowIfCancellationRequested();
                totalBytes += read;
                EnsureCountedBytesWithinBudget(totalBytes, budget);
                processor.Process(buffer, 0, read, cancellationToken);
            }

            return processor.Finish();
        }
    }

    private static void EnsureCountedBytesWithinBudget(long totalBytes, ResourceBudget budget)
    {
        if (totalBytes > budget.MaxFileBytes)
        {
            // A stream without a usable length is refused as soon as the budget is
            // crossed, not after the whole file has been read.
            throw ResourceBudgets.Limit("file is larger than maxFileBytes");
        }
    }

    /// <summary>
    /// BOM precedence is byte-identical to <see cref="TextDocument"/>: UTF-32 LE/BE,
    /// then UTF-16 LE/BE, then UTF-8, otherwise UTF-8 without a BOM. Only the bytes that
    /// actually arrived are considered, so a file shorter than four bytes still selects
    /// the same encoding it would after a whole-payload decode.
    /// </summary>
    private static (Encoding Encoding, int BomLength) Signature(byte[] bytes, int length)
    {
        var span = bytes.AsSpan(0, length);
        if (length >= 4 && span.StartsWith(Utf32LeBom)) return (Utf32Le, 4);
        if (length >= 4 && span.StartsWith(Utf32BeBom)) return (Utf32Be, 4);
        if (length >= 2 && span.StartsWith(Utf16LeBom)) return (Utf16Le, 2);
        if (length >= 2 && span.StartsWith(Utf16BeBom)) return (Utf16Be, 2);
        if (length >= 3 && span.StartsWith(Utf8Bom)) return (Utf8, 3);
        return (Utf8, 0);
    }

    private readonly record struct ContentDigests(string Md5, string Sha256);

    private sealed record ScanResult(LineSink Sink, ContentDigests Digests);

    /// <summary>
    /// Decodes byte chunks strictly, feeds the canonical characters to the line sink and
    /// to the incremental UTF-8 hashes, and never holds more than one chunk of either.
    /// </summary>
    private sealed class ChunkProcessor
    {
        private readonly LineSink _sink;
        private readonly bool _hashContent;
        private readonly char[] _chars = new char[ChunkBytes];
        // Canonical characters handed to the digest; normalization never grows the text,
        // so one chunk-sized buffer is enough. Allocated only on the hashing path: FS-09
        // search scans share this processor without digests, and a traversal of many
        // files must not pay for buffers it never fills.
        private readonly char[]? _canonical;
        // UTF-8 needs at most three bytes per UTF-16 code unit, plus four for a surrogate
        // pair completed from the encoder's pending state. Hashing path only, see above.
        private readonly byte[]? _encoded;
        private readonly IncrementalHash? _md5;
        private readonly IncrementalHash? _sha256;
        // Replacement fallback, i.e. exactly the bytes Encoding.UTF8.GetBytes(canonical)
        // produces. The canonical text is already strictly decoded, so this only matters
        // for a text that could not round-trip. Hashing path only, see above.
        private readonly Encoder? _encoder;
        private Decoder _decoder = Utf8.GetDecoder();
        private bool _nulIsBinary;
        private bool _sawNul;
        private bool _pendingCr;

        internal ChunkProcessor(LineSink sink, bool hashContent)
        {
            _sink = sink;
            _hashContent = hashContent;
            if (hashContent)
            {
                _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                _canonical = new char[ChunkBytes];
                _encoded = new byte[(ChunkBytes * 3) + 4];
                _encoder = Encoding.UTF8.GetEncoder();
            }
        }

        internal void Start(Encoding encoding, bool nulIsBinary)
        {
            _decoder = encoding.GetDecoder();
            _nulIsBinary = nulIsBinary;
        }

        internal void Process(byte[] bytes, int offset, int count, CancellationToken cancellationToken)
        {
            var consumed = 0;
            while (consumed < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _decoder.Convert(
                        bytes.AsSpan(offset + consumed, count - consumed),
                        _chars,
                        flush: false,
                        out var bytesUsed,
                        out var charsUsed,
                        out _);
                    if (bytesUsed == 0 && charsUsed == 0)
                    {
                        // Unreachable: the character buffer is never smaller than one code unit.
                        throw new InvalidOperationException("Streaming decoder made no progress.");
                    }

                    consumed += bytesUsed;
                    Accept(_chars.AsSpan(0, charsUsed));
                }
                catch (DecoderFallbackException ex)
                {
                    throw UnsupportedEncoding(ex);
                }
            }
        }

        /// <summary>
        /// Completes the decode and the line scan, then reports the whole-file digests.
        /// </summary>
        internal ScanResult Finish()
        {
            // Flush the strict decoder: an incomplete trailing sequence must fail exactly
            // like a whole-payload GetString would.
            try
            {
                while (true)
                {
                    _decoder.Convert(
                        ReadOnlySpan<byte>.Empty, _chars, flush: true, out _, out var charsUsed, out var completed);
                    if (charsUsed == 0)
                    {
                        break;
                    }

                    Accept(_chars.AsSpan(0, charsUsed));
                    if (completed)
                    {
                        break;
                    }
                }
            }
            catch (DecoderFallbackException ex)
            {
                // A strict decoder reports a bad sequence as a fallback failure. The FS-03
                // contract is the machine code `unsupported_encoding`, so it is translated
                // here: a platform decoder exception must never reach the client, where it
                // would surface as an unexpected internal defect instead of the documented
                // refusal.
                throw UnsupportedEncoding(ex);
            }

            // The file ended: close the last line (a pending CR terminates it just like a
            // line break would).
            _sink.Complete();

            if (_hashContent)
            {
                // Emits the replacement bytes of a trailing unpaired surrogate, so the
                // streamed digest stays identical to Encoding.UTF8.GetBytes.
                while (true)
                {
                    _encoder!.Convert(
                        ReadOnlySpan<char>.Empty, _encoded!, flush: true, out _, out var bytesUsed, out var completed);
                    AppendDigests(_encoded.AsSpan(0, bytesUsed));
                    if (completed || bytesUsed == 0)
                    {
                        break;
                    }
                }
            }

            if (_sawNul)
            {
                // Deferred to the very end on purpose: TextDocument decodes the whole
                // payload first, so a file that also contains an invalid sequence is
                // reported as unsupported_encoding there and must be here too.
                throw new MutationException("binary_file", "Binary file cannot be mutated as text.");
            }

            var md5 = _md5 is null ? string.Empty : Convert.ToHexString(_md5.GetHashAndReset());
            var sha256 = _sha256 is null ? string.Empty : Convert.ToHexString(_sha256.GetHashAndReset());
            return new ScanResult(_sink, new ContentDigests(md5, sha256));
        }

        /// <summary>
        /// The single translation from a strict-decoder failure to the FS-03 machine code.
        /// Both the chunked decode and the final flush route through it, so an invalid
        /// sequence is reported identically no matter where in the file it sits.
        /// </summary>
        private static MutationException UnsupportedEncoding(DecoderFallbackException exception) =>
            new("unsupported_encoding", "Invalid or unsupported text encoding.", exception);

        private void Accept(ReadOnlySpan<char> chars)
        {
            if (chars.IsEmpty)
            {
                return;
            }

            // A decoded U+0000 is binary only for UTF-8 without a BOM; UTF-16/UTF-32 text
            // legitimately contains NUL characters.
            if (_nulIsBinary && chars.IndexOf('\0') >= 0)
            {
                _sawNul = true;
            }

            if (_hashContent)
            {
                // The digest covers the canonical text, i.e. what
                // FileTextHelper.ComputeContentHashes would hash, so line endings are
                // normalized on the way into the encoder.
                var length = Normalize(chars, _canonical!);
                if (length > 0)
                {
                    Hash(_canonical!.AsSpan(0, length));
                }
            }

            _sink.Append(chars);
        }

        /// <summary>
        /// Streaming equivalent of <see cref="FileTextHelper.NormalizeLineEndings"/>: CRLF
        /// becomes one LF and a lone CR becomes LF. A CR at the end of a chunk is resolved
        /// by the next chunk, so a CRLF split across reads still normalizes to one LF.
        /// </summary>
        private int Normalize(ReadOnlySpan<char> chars, Span<char> destination)
        {
            var length = 0;
            var index = 0;
            if (_pendingCr)
            {
                _pendingCr = false;
                if (chars[0] == '\n')
                {
                    index = 1;
                }
            }

            for (; index < chars.Length; index++)
            {
                var current = chars[index];
                if (current != '\r')
                {
                    destination[length++] = current;
                    continue;
                }

                destination[length++] = '\n';
                if (index + 1 == chars.Length)
                {
                    _pendingCr = true;
                }
                else if (chars[index + 1] == '\n')
                {
                    index++;
                }
            }

            return length;
        }

        private void Hash(ReadOnlySpan<char> chars)
        {
            var offset = 0;
            while (offset < chars.Length)
            {
                _encoder!.Convert(
                    chars[offset..],
                    _encoded!,
                    flush: false,
                    out var charsUsed,
                    out var bytesUsed,
                    out _);
                if (charsUsed == 0)
                {
                    // Unreachable: the byte buffer always fits a full chunk of characters.
                    throw new InvalidOperationException("Streaming encoder made no progress.");
                }

                AppendDigests(_encoded.AsSpan(0, bytesUsed));
                offset += charsUsed;
            }
        }

        private void AppendDigests(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty || _md5 is null || _sha256 is null)
            {
                return;
            }

            _md5.AppendData(bytes);
            _sha256.AppendData(bytes);
        }
    }

    /// <summary>
    /// Canonical line splitter. It counts every line of the file, materializes only the
    /// lines the request selected, and refuses a line that would exceed the line budget
    /// before it can be appended.
    /// </summary>
    private abstract class LineSink(ResourceBudget budget)
    {
        private readonly StringBuilder _line = new();
        private int _lineLength;
        private bool _hasPendingLine;
        private bool _pendingCr;

        internal int TotalLines { get; private set; }

        /// <summary>
        /// Consumes decoded characters, cutting lines on CRLF, LF and lone CR. A CR at
        /// the end of a chunk is held until the next chunk, so a CRLF split across reads
        /// still counts as one break.
        /// </summary>
        internal void Append(ReadOnlySpan<char> chars)
        {
            var index = 0;
            while (index < chars.Length)
            {
                if (_pendingCr)
                {
                    _pendingCr = false;
                    if (chars[index] == '\n')
                    {
                        index++;
                    }

                    EndLine(closedByDelimiter: true);
                    continue;
                }

                var relative = chars[index..].IndexOfAny('\r', '\n');
                if (relative < 0)
                {
                    AppendSegment(chars[index..]);
                    return;
                }

                var at = index + relative;
                if (at > index)
                {
                    AppendSegment(chars[index..at]);
                }

                if (chars[at] == '\r')
                {
                    if (at + 1 == chars.Length)
                    {
                        _pendingCr = true;
                        return;
                    }

                    index = chars[at + 1] == '\n' ? at + 2 : at + 1;
                }
                else
                {
                    index = at + 1;
                }

                EndLine(closedByDelimiter: true);
            }
        }

        /// <summary>
        /// Closes the text at end of file, matching StringReader.ReadLine's count. A pending
        /// CR closes its line as a delimiter; a bare trailing line has no delimiter after it.
        /// </summary>
        internal void Complete()
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                EndLine(closedByDelimiter: true);
            }
            else if (_hasPendingLine)
            {
                EndLine(closedByDelimiter: false);
            }

            OnScanCompleted();
        }

        /// <summary>
        /// Called once after the last line is closed, when <see cref="TotalLines"/> is final;
        /// lets a sink append trailing material that depends on how the last selected line
        /// ended and on the final line count.
        /// </summary>
        internal virtual void OnScanCompleted()
        {
        }

        /// <summary>True when this line belongs to the requested output.</summary>
        protected abstract bool Materialize(int lineNumber);

        protected abstract void Emit(int lineNumber, string text, bool closedByDelimiter);

        private void AppendSegment(ReadOnlySpan<char> segment)
        {
            if (segment.IsEmpty)
            {
                return;
            }

            if (_lineLength + segment.Length > budget.MaxLineChars)
            {
                // Refused before the oversized line becomes a string: the builder holds at
                // most MaxLineChars code units when this fires.
                throw ResourceBudgets.Limit("line is longer than maxLineChars");
            }

            if (Materialize(TotalLines + 1))
            {
                _line.Append(segment);
            }

            _lineLength += segment.Length;
            _hasPendingLine = true;
        }

        private void EndLine(bool closedByDelimiter)
        {
            TotalLines++;
            var text = _line.ToString();
            _line.Clear();
            _lineLength = 0;
            _hasPendingLine = false;
            Emit(TotalLines, text, closedByDelimiter);
        }
    }

    /// <summary>
    /// Selected-text sink. FS-10 selection semantics: selected lines are joined with one LF
    /// between neighbours, a leading blank selected line is kept (the separator decision
    /// tracks "a line was selected", not "the builder is non-empty"), and the LF that closed
    /// the last selected line in the source is part of the answer when it existed — so a
    /// range or a capped prefix is an exact substring of the canonical text and a terminal
    /// newline survives a full-file read.
    /// </summary>
    /// <remarks>
    /// <paramref name="textLimit"/> is the hard bound on the materialized text, in UTF-16
    /// code units. It exists so a full-file read is refused <em>while</em> it is read
    /// instead of after the whole file has become a string: the transport would otherwise
    /// build a payload far past <c>maxResponseChars</c> and only then replace it, which is
    /// exactly the materialization the FS-07 memory bound forbids.
    /// </remarks>
    private sealed class CanonicalSink(
        ResourceBudget budget,
        int? startLine,
        int? endLine,
        bool captureFullText,
        int? maxLines,
        int? textLimit) : LineSink(budget)
    {
        private readonly StringBuilder _selected = new();
        private bool _hasSelection;
        private int _firstSelectedLine;
        private int _lastSelectedLine;
        private bool _lastSelectedClosedByDelimiter;

        internal string SelectedText => _selected.ToString();

        /// <summary>First line actually returned; null when nothing was selected.</summary>
        internal int? ActualStartLine => _hasSelection ? _firstSelectedLine : null;

        /// <summary>Last line actually returned; null when nothing was selected.</summary>
        internal int? ActualEndLine => _hasSelection ? _lastSelectedLine : null;

        /// <summary>FS-10: true only when a cap cut the answer, never for a plain range request.</summary>
        internal bool Truncated { get; private set; }

        /// <summary>FS-10: lines exist in the file after <see cref="ActualEndLine"/>.</summary>
        internal bool HasMore { get; private set; }

        protected override bool Materialize(int lineNumber) =>
            captureFullText
                ? maxLines is null || lineNumber <= maxLines.Value
                : lineNumber >= (startLine ?? 1) && lineNumber <= (endLine ?? int.MaxValue);

        protected override void Emit(int lineNumber, string text, bool closedByDelimiter)
        {
            if (!Materialize(lineNumber))
            {
                return;
            }

            if (textLimit is { } limit)
            {
                var separator = _hasSelection ? 1 : 0;
                // The LF that closes this line belongs to the answer whenever this turns
                // out to be the last selected line, so it is charged against the budget
                // here — while the stream is still being read — instead of after the
                // whole file has been scanned: an over-budget refusal carries neither
                // text nor hash, so there is no tail worth reading (FS-10 / FS-07 R1, R8).
                // For an intermediate selected line this bound never exceeds the final
                // text (what follows only appends), and for the last selected line it is
                // exactly the final length, so no fitting answer is refused by the +1.
                var closingLf = closedByDelimiter ? 1 : 0;
                if (_selected.Length + separator + text.Length + closingLf > limit)
                {
                    throw ResourceBudgets.Limit("the selected text is larger than maxResponseChars");
                }
            }

            if (_hasSelection)
            {
                _selected.Append('\n');
            }

            _selected.Append(text);
            if (!_hasSelection)
            {
                _hasSelection = true;
                _firstSelectedLine = lineNumber;
            }

            _lastSelectedLine = lineNumber;
            _lastSelectedClosedByDelimiter = closedByDelimiter;
        }

        internal override void OnScanCompleted()
        {
            if (!_hasSelection)
            {
                return;
            }

            // The delimiter that closed the last selected line in the source belongs to the
            // answer. It is appended here, after the scan, because only the end of the file
            // proves the last selected line had no delimiter after it.
            if (_lastSelectedClosedByDelimiter)
            {
                if (textLimit is { } limit && _selected.Length + 1 > limit)
                {
                    throw ResourceBudgets.Limit("the selected text is larger than maxResponseChars");
                }

                _selected.Append('\n');
            }

            HasMore = _lastSelectedLine < TotalLines;
            Truncated = captureFullText && HasMore;
        }
    }

    private sealed class AllLinesSink(ResourceBudget budget) : LineSink(budget)
    {
        private readonly List<string> _lines = [];

        internal IReadOnlyList<string> Lines => _lines;

        protected override bool Materialize(int lineNumber) => true;

        protected override void Emit(int lineNumber, string text, bool closedByDelimiter) => _lines.Add(text);
    }

    /// <summary>
    /// FS-09 search sink: hands each completed canonical line to the caller's callback
    /// with its 1-based number instead of collecting anything, so the search tool's
    /// memory stays one line regardless of the file size. The line budget is inherited
    /// from <see cref="LineSink"/>: an oversized line is refused while it is being
    /// assembled, before it can become a string.
    /// </summary>
    private sealed class SearchLineSink(ResourceBudget budget, Action<int, string> emit) : LineSink(budget)
    {
        protected override bool Materialize(int lineNumber) => true;

        protected override void Emit(int lineNumber, string text, bool closedByDelimiter) => emit(lineNumber, text);
    }
}
