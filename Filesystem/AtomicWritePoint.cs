namespace FilesystemMcp;

internal enum AtomicWritePoint
{
    BeforeLock, LockContended, DuringWrite, BeforeFlush, AfterTempWrite, BeforeCommit, AfterCommit, Cleanup
}
