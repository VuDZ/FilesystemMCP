namespace FilesystemMcp;

/// <summary>
/// One stdio frame read attempt: the decoded text, or why the frame cannot be used.
/// An oversized frame and a frame that is not valid UTF-8 are both rejected, but they
/// are distinct machine outcomes (invalid request vs parse error), so they stay apart.
/// </summary>
internal readonly record struct FrameReadResult(string? Text, bool IsOversized, bool IsMalformed, bool EndOfStream)
{
    public static FrameReadResult Oversized { get; } = new(null, true, false, false);
    public static FrameReadResult Malformed { get; } = new(null, false, true, false);
    public static FrameReadResult End { get; } = new(null, false, false, true);
    public static FrameReadResult Frame(string text) => new(text, false, false, false);
}
