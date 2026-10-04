using System.Buffers;
using System.Text;

namespace FilesystemMcp;

/// <summary>
/// Bounded UTF-8 frame reader for the stdio transport. It never materializes more
/// than the frame budget (plus one read buffer), so an oversized or endless line
/// cannot exhaust memory. An oversized frame is drained up to its LF and reported as
/// <see cref="FrameReadResult.Oversized"/> instead of being parsed or executed, and
/// the next frame is read normally — the stream stays usable (FS-07).
/// </summary>
/// <remarks>
/// Reads bytes rather than text so the budget is a byte budget, exactly as
/// documented, and decodes strictly: malformed UTF-8 in a frame is a frame-level
/// failure, never a silently replaced character inside a tool argument. Carriage
/// return is stripped only when it is the byte directly before the LF, matching the
/// line-oriented reference transport.
/// </remarks>
internal sealed class BoundedFrameReader
{
    private const int ReadBufferSize = 8192;

    public BoundedFrameReader(Stream input, long maxFrameBytes)
    {
        if (maxFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
        }

        _input = input;
        _maxFrameBytes = maxFrameBytes;
        _frame = new MemoryStream(capacity: (int)Math.Min(maxFrameBytes, 64 * 1024));
    }

    /// <summary>
    /// Reads the next frame. A frame longer than the budget is fully drained before
    /// returning, so the following call starts at the next line boundary.
    /// </summary>
    public async ValueTask<FrameReadResult> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        _frame.SetLength(0);
        var oversized = false;
        while (true)
        {
            if (_bufferOffset >= _bufferCount)
            {
                if (_endOfStream)
                {
                    return FinishFrame(oversized, atEndOfStream: true);
                }

                _bufferCount = await _input.ReadAsync(_readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _bufferOffset = 0;
                if (_bufferCount == 0)
                {
                    _endOfStream = true;
                    return FinishFrame(oversized, atEndOfStream: true);
                }
            }

            var span = _readBuffer.AsSpan(_bufferOffset, _bufferCount - _bufferOffset);
            var newline = span.IndexOf((byte)'\n');
            if (newline < 0)
            {
                Append(span, ref oversized);
                _bufferOffset = _bufferCount;
                continue;
            }

            Append(span[..newline], ref oversized);
            _bufferOffset += newline + 1;
            return FinishFrame(oversized, atEndOfStream: false, terminatedByLf: true);
        }
    }

    private void Append(ReadOnlySpan<byte> bytes, ref bool oversized)
    {
        if (oversized || bytes.Length == 0)
        {
            return;
        }

        if (_pendingCarriageReturn)
        {
            _pendingCarriageReturn = false;
            if (bytes[0] == (byte)'\n')
            {
                // The CR was the first half of a CRLF split across two reads: it is part
                // of the terminator and is dropped. The budget below therefore counts a
                // frame, never the CR of the line ending that delimits it.
                bytes = bytes[1..];
            }
            else
            {
                Store([(byte)'\r'], ref oversized);
                if (oversized)
                {
                    return;
                }
            }
        }

        if (bytes.Length == 0)
        {
            return;
        }

        // A CR at the very end of this segment may still turn out to be the first half of
        // a CRLF, so it is held back until the next byte decides. This is the only reason
        // the reader ever buffers a terminator byte.
        if (bytes[^1] == (byte)'\r')
        {
            _pendingCarriageReturn = true;
            bytes = bytes[..^1];
        }

        Store(bytes, ref oversized);
    }

    private void Store(ReadOnlySpan<byte> bytes, ref bool oversized)
    {
        if (bytes.Length <= _maxFrameBytes - _frame.Length)
        {
            _frame.Write(bytes);
            return;
        }

        // The budget is exceeded. An oversized frame is never executed, so the bytes
        // still inside the budget are not kept either: dropping them immediately caps
        // this reader's memory at the budget while the rest of the line is drained.
        oversized = true;
    }

    private FrameReadResult FinishFrame(bool oversized, bool atEndOfStream, bool terminatedByLf = false)
    {
        if (atEndOfStream && !terminatedByLf)
        {
            // FS-07 R10: stdin closed before LF. The partial frame is discarded and the
            // process exits without a response — a parse error here would answer a peer
            // that is already gone. A pending CR belongs to that same unfinished frame.
            _pendingCarriageReturn = false;
            return FrameReadResult.End;
        }

        if (!oversized && _pendingCarriageReturn && !terminatedByLf)
        {
            // A finish that is neither EOF nor LF. EOF already returned above, and a LF
            // drops a held CR as the first half of CRLF. A bare CR that is payload is
            // stored only on this path.
            Store([(byte)'\r'], ref oversized);
        }

        _pendingCarriageReturn = false;

        if (oversized)
        {
            return FrameReadResult.Oversized;
        }

        if (_frame.Length == 0)
        {
            return atEndOfStream ? FrameReadResult.End : FrameReadResult.Frame(string.Empty);
        }

        var bytes = _frame.GetBuffer().AsSpan(0, (int)_frame.Length);

        if (_firstFrame)
        {
            _firstFrame = false;
            // Console.In historically consumed a UTF-8 BOM before the first frame.
            // Byte-level reading must reproduce that, or a BOM-writing client would
            // turn its first request into a parse error.
            if (bytes.StartsWith(Encoding.UTF8.Preamble))
            {
                bytes = bytes[Encoding.UTF8.Preamble.Length..];
            }
        }

        if (bytes.Length == 0)
        {
            return atEndOfStream ? FrameReadResult.End : FrameReadResult.Frame(string.Empty);
        }

        try
        {
            var charCount = _decoder.GetCharCount(bytes, flush: true);
            var chars = ArrayPool<char>.Shared.Rent(Math.Max(charCount, 1));
            try
            {
                var written = _decoder.GetChars(bytes, chars, flush: true);
                return FrameReadResult.Frame(new string(chars, 0, written));
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
            }
        }
        catch (DecoderFallbackException)
        {
            // Invalid UTF-8: the frame is not usable text, but the stream is still
            // aligned at this LF, so the session survives.
            return FrameReadResult.Malformed;
        }
        finally
        {
            _decoder.Reset();
        }
    }

    private readonly Stream _input;
    private readonly long _maxFrameBytes;
    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly byte[] _readBuffer = new byte[ReadBufferSize];
    private readonly MemoryStream _frame;
    private int _bufferOffset;
    private int _bufferCount;
    private bool _endOfStream;
    private bool _firstFrame = true;
    private bool _pendingCarriageReturn;
}
