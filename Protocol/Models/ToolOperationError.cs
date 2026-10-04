namespace FilesystemMcp;

// FS-05 error object: the machine code is the API, the message is advisory and
// details stay bounded (a safe relative path and the retryable flag at most).
// truncationReason is the FS-07 addition: it preserves the cause of an already-cut
// partial result when the transport has to refuse the frame itself.
internal sealed record ToolOperationError(string Code, string Message, ToolErrorDetails? Details = null);
