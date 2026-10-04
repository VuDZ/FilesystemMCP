namespace FilesystemMcp;

internal readonly record struct DirectoryIdentity(ulong Volume, ulong Low, ulong High);
