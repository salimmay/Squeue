using System.Runtime.InteropServices;

namespace Squeue.Bench;

/// Windows' own copy engine, the one Explorer builds on.
internal static partial class WindowsCopy
{
    [LibraryImport("kernel32.dll", EntryPoint = "CopyFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CopyFileEx(string source, string destination, nint progress, nint data, nint cancel, uint flags);

    private const uint COPY_FILE_FAIL_IF_EXISTS = 0x1;

    public static void Copy(string source, string destination)
    {
        if (!CopyFileEx(source, destination, 0, 0, 0, COPY_FILE_FAIL_IF_EXISTS))
            throw new IOException($"CopyFileEx failed for '{source}' (Windows error {Marshal.GetLastPInvokeError()}).");
    }
}
