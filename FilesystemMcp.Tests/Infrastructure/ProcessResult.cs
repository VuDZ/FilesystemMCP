namespace FilesystemMcp.Tests.Infrastructure;

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
