namespace Squeue.Core.FileSystem;

/// A failed Win32 file operation, with the error code kept for decisions.
public sealed class FsException(string operation, string path, int win32Error)
    : IOException($"Couldn't {operation} '{path}' (Windows error {win32Error}).")
{
    public string Operation { get; } = operation;
    public string FilePath { get; } = path;
    public int Win32Error { get; } = win32Error;

    public bool IsNotFound => Win32Error is Native.ERROR_FILE_NOT_FOUND or Native.ERROR_PATH_NOT_FOUND;
    public bool IsSharingViolation => Win32Error == Native.ERROR_SHARING_VIOLATION;
    public bool IsAccessDenied => Win32Error == Native.ERROR_ACCESS_DENIED;
    public bool IsAlreadyExists => Win32Error is Native.ERROR_FILE_EXISTS or Native.ERROR_ALREADY_EXISTS;
}
