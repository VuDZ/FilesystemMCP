namespace FilesystemMcp;

/// <summary>One diagnostic record before it is turned into a line.</summary>
internal readonly record struct LogEntry(DateTimeOffset TimestampUtc, string Level, string Message, string? CorrelationId);
