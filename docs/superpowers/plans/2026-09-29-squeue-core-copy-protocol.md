# Squeue Core Copy Protocol Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Squeue.Core`'s crash-safe single-file copy protocol — journal, temp files, flush-then-publish, unbuffered verification, and reconciliation after a crash — proven by a fault-injection test suite.

**Architecture:** A thin `IFileSystem` interface wraps the handful of Win32 file operations the protocol needs (`WindowsFileSystem` is the real implementation; `FaultyFileSystem` in the tests injects crashes at every boundary). A SQLite `Journal` records intent before each filesystem change. `FileCopier` runs the per-file protocol for one entry; `Reconciler` compares open journal attempts against what is actually on disk after an interruption. No UI, no scheduler, no threads yet.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), xUnit, `Microsoft.Data.Sqlite`, `System.IO.Hashing`, hand-written `LibraryImport` P/Invoke for kernel32.

**Spec:** Squeue — System Design Spec, Revision 3: https://claude.ai/code/artifact/e58e5773-986c-4724-8466-07c85a154be9 — sections "Safety model and invariants", "Data model", "Copy protocol, moves and recovery".

## Plan series

This is plan 1 of 8. Each later plan gets its own document.

1. **Core copy protocol** (this plan) — single-file copy, journal, reconciliation, fault injection.
2. Moves — protected source delete, same-volume rename, directory entries.
3. Throughput — pipelined reader/writer threads, batched commits, benchmark harness (the spec's spike).
4. File contents — alternate data streams, EA/EFS/reparse-point detection, preservation policy.
5. Coordinator — endpoints and disk resolution, scheduler, capacity ledger, destination claims, job runner.
6. Planning — background scans, conflict rules, selection.
7. WPF app — queue view, plan window, compact window, history.
8. Integration — CLI, named pipe, Explorer verbs, Velopack installer.

**Deliberate simplifications in this plan** (replaced by later plans, no users exist yet so the schema can change freely):
- Entries store absolute paths, not endpoint + relative path (plan 5).
- File identities are stored as 60-byte blobs, not separate columns.
- Every journal step is its own transaction (batching comes in plan 3).
- The copy loop reads then writes on one thread (pipelining comes in plan 3).
- Conflict actions are only `Create` and `Replace`; rule evaluation (skip, rename, overwrite older) is plan 6.

## Global Constraints

- Windows 10/11 x64 only; projects target `net10.0-windows`; nothing requires admin rights.
- Default hash algorithm is `xxh3` (XxHash3, 64-bit). Supported names: `xxh3`, `xxh128`, `crc32`, `md5`, `sha1`, `sha256`.
- Temp file names are `~tq` + 10 characters from `[a-z2-7]` + `.tmp`, created in the destination folder, and journaled **before** the file is created.
- Unflushed data never gets the final name: `FlushFileBuffers` on the temp before the rename, and again after it.
- When verification is on, an existing destination is replaced only by a temp file that has already passed verification.
- Cleanup deletes only objects whose file ID matches the journal; anything uncertain is left in place and reported.
- SQLite runs with `journal_mode = WAL` and `synchronous = FULL`.
- Long paths (over 260 characters) and Unicode names must work; every Win32 call uses a `\\?\` path.
- Chunk sizes are positive multiples of 4096 (unbuffered reads require sector alignment).
- Every commit message ends with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

Inputs the spec implies but that are easy to miss, most likely first. Each has a test in the owning task.

1. **Source open in another program for writing** (a camera app, an editor still saving) → the entry fails with "The source is in use by another program.", the destination is untouched and no temp file is left. Test in Task 8: `Source_in_use_by_another_program_fails_with_reason`.
2. **Destination edited by someone else between the conflict decision and publishing** → nothing is replaced; outcome `DestinationChanged`. Test in Task 8: `Replace_keeps_a_destination_that_changed_after_the_decision`.
3. **Destination file appears after planning for a new-file copy** → the rename refuses to overwrite it; outcome `DestinationChanged`. Test in Task 8: `Create_keeps_a_destination_that_appeared_after_planning`.
4. **Paths over 260 characters with Unicode names** (`Café 写真 📷.NEF` deep in a folder tree) → copies and verifies normally. Test in Task 8: `Copies_long_paths_with_unicode_names`.
5. **Zero-byte files and sizes exactly on chunk and sector boundaries** → copy and unbuffered verification agree. Tests in Task 3 (`Unbuffered_read_returns_exact_content`) and Task 8 (`Copies_edge_sizes`).

---

## File structure

```
Squeue.sln
.gitignore
src/Squeue.Core/Squeue.Core.csproj
src/Squeue.Core/FileSystem/Native.cs              P/Invoke declarations, constants, identity reader
src/Squeue.Core/FileSystem/FsException.cs         Win32 error wrapper with IsNotFound / IsSharingViolation / ...
src/Squeue.Core/FileSystem/FileIdentity.cs        file object + version (id, size, times, attributes)
src/Squeue.Core/FileSystem/IFileSystem.cs         the operations the protocol needs
src/Squeue.Core/FileSystem/WindowsFileSystem.cs   real implementation + handle classes
src/Squeue.Core/FileSystem/AlignedBuffer.cs       4096-aligned native buffer for unbuffered reads
src/Squeue.Core/Copy/Hashers.cs                   streaming hash algorithms by name
src/Squeue.Core/Copy/TempNames.cs                 random temp file names
src/Squeue.Core/Copy/CopyTypes.cs                 CopyOptions, CopyOutcome, CopyResult
src/Squeue.Core/Copy/FileCopier.cs                the per-file protocol
src/Squeue.Core/Copy/Reconciler.cs                post-crash reconciliation
src/Squeue.Core/State/Records.cs                  enums + Entry/Attempt records
src/Squeue.Core/State/IdentityBlob.cs             FileIdentity <-> bytes
src/Squeue.Core/State/Journal.cs                  SQLite journal
tests/Squeue.Tests/Squeue.Tests.csproj
tests/Squeue.Tests/TestDir.cs                     temp folder helper
tests/Squeue.Tests/CopyScenario.cs                journal + source/dest paths for copier tests
tests/Squeue.Tests/Fakes/FaultyFileSystem.cs      crash injection + read corruption
tests/Squeue.Tests/FileSystem/IdentityTests.cs
tests/Squeue.Tests/FileSystem/TempFileTests.cs
tests/Squeue.Tests/FileSystem/VerifyReadTests.cs
tests/Squeue.Tests/Copy/HasherTests.cs
tests/Squeue.Tests/Copy/TempNameTests.cs
tests/Squeue.Tests/State/JournalTests.cs
tests/Squeue.Tests/Fakes/FaultyFileSystemTests.cs
tests/Squeue.Tests/Copy/FileCopierTests.cs
tests/Squeue.Tests/Copy/ReconcilerTests.cs
tests/Squeue.Tests/Copy/CrashMatrixTests.cs
```

---

### Task 1: Solution scaffold, file identity and source open

**Files:**
- Create: `Squeue.sln`, `.gitignore`, `src/Squeue.Core/Squeue.Core.csproj`, `tests/Squeue.Tests/Squeue.Tests.csproj`
- Create: `src/Squeue.Core/FileSystem/Native.cs`, `FsException.cs`, `FileIdentity.cs`, `IFileSystem.cs`, `WindowsFileSystem.cs`
- Create: `tests/Squeue.Tests/TestDir.cs`, `tests/Squeue.Tests/FileSystem/IdentityTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `readonly record struct FileIdentity(ulong VolumeSerial, UInt128 FileId, long Size, long CreationTime, long LastWriteTime, long ChangeTime, uint Attributes)` with `bool IsSameObject(FileIdentity)`, `bool IsSameVersion(FileIdentity)`, `static byte[] FileIdToBytes(UInt128)`, `static UInt128 FileIdFromBytes(ReadOnlySpan<byte>)`. Times are FILETIME (100 ns ticks since 1601, UTC).
  - `sealed class FsException : IOException` with `int Win32Error`, `bool IsNotFound`, `IsSharingViolation`, `IsAccessDenied`, `IsAlreadyExists`.
  - `interface IFileSystem { ISourceFile OpenSource(string); ITempFile CreateTemp(string); IVerifyFile OpenForVerify(string); FileIdentity? TryGetIdentity(string); bool DeleteIfSameObject(string, UInt128); void CreateDirectory(string); }`
  - `interface ISourceFile : IDisposable { FileIdentity Identity { get; } int Read(Span<byte> buffer, long offset); }`
  - `interface ITempFile : IDisposable { FileIdentity GetIdentity(); void Preallocate(long); void Write(ReadOnlySpan<byte>, long); void SetLength(long); void SetTimesAndAttributes(long creationTime, long lastWriteTime, uint attributes); void Flush(); void RenameTo(string finalPath, bool replaceExisting); }`
  - `interface IVerifyFile : IDisposable { int Read(Span<byte> buffer, long offset); }`
  - `sealed class WindowsFileSystem : IFileSystem` (this task implements `OpenSource`, `TryGetIdentity`, `CreateDirectory`).
  - Test helper `sealed class TestDir : IDisposable` with `string Path`, `string PathOf(string)`, `string Write(string, byte[])`, `static byte[] RandomBytes(int length, int seed = 1)`.

- [ ] **Step 1: Check the .NET 10 SDK is installed**

Run: `dotnet --list-sdks`
Expected: a line starting with `10.`. If there is none, install it with `winget install Microsoft.DotNet.SDK.10` and open a new terminal.

- [ ] **Step 2: Create the repository, solution and projects**

Run from the project root (`E:\Data From Desktop\Coding\TeraCopy`):

```bash
git init
git checkout -b feat/core-copy-protocol
dotnet new gitignore
dotnet new sln -n Squeue
dotnet new classlib -n Squeue.Core -o src/Squeue.Core -f net10.0
dotnet new xunit -n Squeue.Tests -o tests/Squeue.Tests -f net10.0
dotnet sln add src/Squeue.Core/Squeue.Core.csproj tests/Squeue.Tests/Squeue.Tests.csproj
dotnet add tests/Squeue.Tests/Squeue.Tests.csproj reference src/Squeue.Core/Squeue.Core.csproj
dotnet add src/Squeue.Core/Squeue.Core.csproj package Microsoft.Data.Sqlite
dotnet add src/Squeue.Core/Squeue.Core.csproj package System.IO.Hashing
```

Delete the template files `src/Squeue.Core/Class1.cs` and `tests/Squeue.Tests/UnitTest1.cs`.

- [ ] **Step 3: Set project properties**

In `src/Squeue.Core/Squeue.Core.csproj`, replace the first `<PropertyGroup>` with:

```xml
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RootNamespace>Squeue.Core</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Squeue.Tests" />
  </ItemGroup>
```

In `tests/Squeue.Tests/Squeue.Tests.csproj`, change `<TargetFramework>net10.0</TargetFramework>` to `<TargetFramework>net10.0-windows</TargetFramework>` and add `<Nullable>enable</Nullable>` to the same `<PropertyGroup>` if it isn't there.

- [ ] **Step 4: Write the test helper**

`tests/Squeue.Tests/TestDir.cs`:

```csharp
namespace Squeue.Tests;

/// A unique temporary folder, deleted on Dispose.
public sealed class TestDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "squeue-tests", Guid.NewGuid().ToString("N"));

    public TestDir() => Directory.CreateDirectory(Path);

    public string PathOf(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, byte[] content)
    {
        string path = PathOf(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public static byte[] RandomBytes(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 5: Write the failing tests**

`tests/Squeue.Tests/FileSystem/IdentityTests.cs`:

```csharp
using Squeue.Core.FileSystem;

namespace Squeue.Tests.FileSystem;

public class IdentityTests
{
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void Identity_is_stable_for_one_file_and_differs_between_files()
    {
        using var dir = new TestDir();
        string a = dir.Write("a.bin", [1, 2, 3]);
        string b = dir.Write("b.bin", [1, 2, 3]);

        var a1 = _fs.TryGetIdentity(a)!.Value;
        var a2 = _fs.TryGetIdentity(a)!.Value;
        var b1 = _fs.TryGetIdentity(b)!.Value;

        Assert.True(a1.IsSameVersion(a2));
        Assert.False(a1.IsSameObject(b1));
        Assert.Equal(3, a1.Size);
    }

    [Fact]
    public void Rewriting_a_file_changes_its_version_even_if_the_write_time_is_restored()
    {
        using var dir = new TestDir();
        string path = dir.Write("a.bin", [1, 2, 3]);
        var before = _fs.TryGetIdentity(path)!.Value;
        var originalWriteTime = File.GetLastWriteTimeUtc(path);

        Thread.Sleep(50);
        File.WriteAllBytes(path, [9, 9, 9]);
        File.SetLastWriteTimeUtc(path, originalWriteTime);
        var after = _fs.TryGetIdentity(path)!.Value;

        Assert.True(before.IsSameObject(after));
        Assert.Equal(before.LastWriteTime, after.LastWriteTime);
        Assert.False(before.IsSameVersion(after)); // ChangeTime moved
    }

    [Fact]
    public void TryGetIdentity_returns_null_when_nothing_is_there()
    {
        using var dir = new TestDir();
        Assert.Null(_fs.TryGetIdentity(dir.PathOf("missing.bin")));
        Assert.Null(_fs.TryGetIdentity(dir.PathOf(@"no\such\folder\missing.bin")));
    }

    [Fact]
    public void OpenSource_reads_the_content_and_reports_its_identity()
    {
        using var dir = new TestDir();
        var content = TestDir.RandomBytes(100_000);
        string path = dir.Write("a.bin", content);

        using var source = _fs.OpenSource(path);
        var read = new byte[content.Length];
        long offset = 0;
        int n;
        while ((n = source.Read(read.AsSpan((int)offset), offset)) > 0) offset += n;

        Assert.Equal(content, read);
        Assert.Equal(content.Length, source.Identity.Size);
    }

    [Fact]
    public void OpenSource_blocks_other_writers_while_open()
    {
        using var dir = new TestDir();
        string path = dir.Write("a.bin", [1]);

        using var source = _fs.OpenSource(path);

        Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
    }

    [Fact]
    public void OpenSource_reports_a_sharing_violation_when_another_program_is_writing()
    {
        using var dir = new TestDir();
        string path = dir.Write("a.bin", [1]);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var ex = Assert.Throws<FsException>(() => _fs.OpenSource(path));

        Assert.True(ex.IsSharingViolation);
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail**

Run: `dotnet test`
Expected: build FAILS — `WindowsFileSystem`, `FsException` and `FileIdentity` don't exist.

- [ ] **Step 7: Write the implementation**

`src/Squeue.Core/FileSystem/FileIdentity.cs`:

```csharp
using System.Buffers.Binary;

namespace Squeue.Core.FileSystem;

/// Which file object this is (volume + file id) and which version of it (size, times).
/// Times are FILETIME values: 100 ns ticks since 1601-01-01 UTC.
public readonly record struct FileIdentity(
    ulong VolumeSerial,
    UInt128 FileId,
    long Size,
    long CreationTime,
    long LastWriteTime,
    long ChangeTime,
    uint Attributes)
{
    public bool IsSameObject(FileIdentity other) =>
        VolumeSerial == other.VolumeSerial && FileId == other.FileId;

    /// ChangeTime moves on any write or metadata change, even when an app restores LastWriteTime.
    public bool IsSameVersion(FileIdentity other) =>
        IsSameObject(other) && Size == other.Size && LastWriteTime == other.LastWriteTime && ChangeTime == other.ChangeTime;

    public static byte[] FileIdToBytes(UInt128 fileId)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(bytes, fileId);
        return bytes;
    }

    public static UInt128 FileIdFromBytes(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt128LittleEndian(bytes);
}
```

`src/Squeue.Core/FileSystem/FsException.cs`:

```csharp
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
```

`src/Squeue.Core/FileSystem/Native.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Squeue.Core.FileSystem;

internal static unsafe partial class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint DELETE = 0x00010000;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;

    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint FILE_SHARE_DELETE = 0x4;
    public const uint FILE_SHARE_ALL = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

    public const uint CREATE_NEW = 1;
    public const uint OPEN_EXISTING = 3;

    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    // FILE_INFO_BY_HANDLE_CLASS values
    public const int FileBasicInfo = 0;
    public const int FileStandardInfo = 1;
    public const int FileRenameInfo = 3;
    public const int FileDispositionInfo = 4;
    public const int FileAllocationInfo = 5;
    public const int FileEndOfFileInfo = 6;
    public const int FileIdInfo = 18;
    public const int FileDispositionInfoEx = 21;

    public const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    public const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;
    public const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_FILE_EXISTS = 80;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_ALREADY_EXISTS = 183;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle file, BY_HANDLE_FILE_INFORMATION* info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushFileBuffers(SafeFileHandle file);

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_STANDARD_INFO
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public fixed byte FileId[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    /// Full path with the \\?\ prefix so paths over 260 characters work.
    public static string LongPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full[2..];
        return @"\\?\" + full;
    }

    public static FsException LastError(string operation, string path) =>
        new(operation, path, Marshal.GetLastPInvokeError());

    public static FileIdentity ReadIdentity(SafeFileHandle handle, string path)
    {
        FILE_BASIC_INFO basic;
        FILE_STANDARD_INFO standard;
        if (!GetFileInformationByHandleEx(handle, FileBasicInfo, &basic, (uint)sizeof(FILE_BASIC_INFO)))
            throw LastError("read the attributes of", path);
        if (!GetFileInformationByHandleEx(handle, FileStandardInfo, &standard, (uint)sizeof(FILE_STANDARD_INFO)))
            throw LastError("read the size of", path);

        ulong volume;
        UInt128 fileId;
        FILE_ID_INFO id;
        if (GetFileInformationByHandleEx(handle, FileIdInfo, &id, (uint)sizeof(FILE_ID_INFO)))
        {
            volume = id.VolumeSerialNumber;
            fileId = FileIdentity.FileIdFromBytes(new ReadOnlySpan<byte>(id.FileId, 16));
        }
        else
        {
            // FAT/exFAT and some network filesystems don't support FileIdInfo; fall back to the 64-bit index.
            BY_HANDLE_FILE_INFORMATION info;
            if (!GetFileInformationByHandle(handle, &info)) throw LastError("read the file id of", path);
            volume = info.VolumeSerialNumber;
            fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        }

        return new FileIdentity(volume, fileId, standard.EndOfFile,
            basic.CreationTime, basic.LastWriteTime, basic.ChangeTime, basic.FileAttributes);
    }
}
```

`src/Squeue.Core/FileSystem/IFileSystem.cs`:

```csharp
namespace Squeue.Core.FileSystem;

/// The file operations the copy protocol needs. Tests wrap it to inject crashes.
public interface IFileSystem
{
    /// Opens a source for reading. Others can still read it, but can't write or delete it while this is open.
    ISourceFile OpenSource(string path);

    /// Creates a new temp file (fails if the name exists), open for read, write and rename.
    ITempFile CreateTemp(string path);

    /// Opens a file for unbuffered reading, bypassing the Windows file cache.
    IVerifyFile OpenForVerify(string path);

    /// Identity of whatever is at the path now, without following reparse points. Null if nothing is there.
    FileIdentity? TryGetIdentity(string path);

    /// Deletes the path only if it is still the object with this file id. False if it's missing or a different object.
    bool DeleteIfSameObject(string path, UInt128 fileId);

    void CreateDirectory(string path);
}

public interface ISourceFile : IDisposable
{
    FileIdentity Identity { get; }
    int Read(Span<byte> buffer, long offset);
}

public interface ITempFile : IDisposable
{
    FileIdentity GetIdentity();
    void Preallocate(long size);
    void Write(ReadOnlySpan<byte> data, long offset);
    void SetLength(long length);
    void SetTimesAndAttributes(long creationTime, long lastWriteTime, uint attributes);
    void Flush();

    /// Renames through this handle. Throws FsException with IsAlreadyExists when the target exists and replaceExisting is false.
    void RenameTo(string finalPath, bool replaceExisting);
}

public interface IVerifyFile : IDisposable
{
    /// The buffer must come from AlignedBuffer and the offset must be a multiple of 4096.
    int Read(Span<byte> buffer, long offset);
}
```

`src/Squeue.Core/FileSystem/WindowsFileSystem.cs`:

```csharp
using Microsoft.Win32.SafeHandles;

namespace Squeue.Core.FileSystem;

public sealed class WindowsFileSystem : IFileSystem
{
    public ISourceFile OpenSource(string path)
    {
        var handle = Open(path, Native.GENERIC_READ, Native.FILE_SHARE_READ, Native.OPEN_EXISTING,
            Native.FILE_FLAG_SEQUENTIAL_SCAN, "open");
        try { return new WindowsSourceFile(handle, Native.ReadIdentity(handle, path)); }
        catch { handle.Dispose(); throw; }
    }

    public ITempFile CreateTemp(string path) => throw new NotImplementedException("Task 2");

    public IVerifyFile OpenForVerify(string path) => throw new NotImplementedException("Task 3");

    public FileIdentity? TryGetIdentity(string path)
    {
        SafeFileHandle handle;
        try
        {
            handle = Open(path, Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, Native.OPEN_EXISTING,
                Native.FILE_FLAG_BACKUP_SEMANTICS | Native.FILE_FLAG_OPEN_REPARSE_POINT, "inspect");
        }
        catch (FsException ex) when (ex.IsNotFound)
        {
            return null;
        }
        using (handle) return Native.ReadIdentity(handle, path);
    }

    public bool DeleteIfSameObject(string path, UInt128 fileId) => throw new NotImplementedException("Task 2");

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    internal static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags, string operation)
    {
        var handle = Native.CreateFile(Native.LongPath(path), access, share, 0, disposition,
            flags | Native.FILE_ATTRIBUTE_NORMAL, 0);
        if (handle.IsInvalid)
        {
            var error = Native.LastError(operation, path);
            handle.Dispose();
            throw error;
        }
        return handle;
    }
}

internal sealed class WindowsSourceFile(SafeFileHandle handle, FileIdentity identity) : ISourceFile
{
    public FileIdentity Identity { get; } = identity;
    public int Read(Span<byte> buffer, long offset) => RandomAccess.Read(handle, buffer, offset);
    public void Dispose() => handle.Dispose();
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 6 tests.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: scaffold Squeue.Core with file identity and protected source open" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Temp file operations and owned cleanup

**Files:**
- Modify: `src/Squeue.Core/FileSystem/WindowsFileSystem.cs` (replace `CreateTemp` and `DeleteIfSameObject`; add `WindowsTempFile`)
- Test: `tests/Squeue.Tests/FileSystem/TempFileTests.cs`

**Interfaces:**
- Consumes: `Native`, `FsException`, `FileIdentity`, `WindowsFileSystem.Open` from Task 1.
- Produces: working `WindowsFileSystem.CreateTemp(string) : ITempFile` (share mode: read only, so a verify handle can read it but nobody can write or delete it) and `WindowsFileSystem.DeleteIfSameObject(string, UInt128) : bool` (deletes read-only files too).

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/FileSystem/TempFileTests.cs`:

```csharp
using Squeue.Core.FileSystem;

namespace Squeue.Tests.FileSystem;

public class TempFileTests
{
    private static readonly long SomeTime = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToFileTimeUtc();
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void CreateTemp_refuses_an_existing_name()
    {
        using var dir = new TestDir();
        string path = dir.Write("~tqexisting.tmp", [1]);

        var ex = Assert.Throws<FsException>(() => _fs.CreateTemp(path));

        Assert.True(ex.IsAlreadyExists);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_flush_and_rename_publish_the_exact_content_and_times()
    {
        using var dir = new TestDir();
        var content = TestDir.RandomBytes(10_000);
        string temp = dir.PathOf("~tqtemp.tmp");
        string final = dir.PathOf("final.bin");

        UInt128 tempId;
        using (var file = _fs.CreateTemp(temp))
        {
            tempId = file.GetIdentity().FileId;
            file.Preallocate(1024 * 1024);
            file.Write(content, 0);
            file.SetLength(content.Length);
            file.SetTimesAndAttributes(SomeTime, SomeTime, 0);
            file.Flush();
            file.RenameTo(final, replaceExisting: false);
            file.Flush();
        }

        Assert.False(File.Exists(temp));
        Assert.Equal(content, File.ReadAllBytes(final));
        Assert.Equal(SomeTime, File.GetLastWriteTimeUtc(final).ToFileTimeUtc());
        Assert.Equal(SomeTime, File.GetCreationTimeUtc(final).ToFileTimeUtc());
        Assert.Equal(tempId, _fs.TryGetIdentity(final)!.Value.FileId);
    }

    [Fact]
    public void RenameTo_without_replace_refuses_an_existing_target_and_keeps_both()
    {
        using var dir = new TestDir();
        string final = dir.Write("final.bin", [7]);
        string temp = dir.PathOf("~tqtemp.tmp");

        using (var file = _fs.CreateTemp(temp))
        {
            file.Write([1, 2], 0);
            var ex = Assert.Throws<FsException>(() => file.RenameTo(final, replaceExisting: false));
            Assert.True(ex.IsAlreadyExists);
        }

        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(final));
        Assert.True(File.Exists(temp));
    }

    [Fact]
    public void RenameTo_with_replace_swaps_in_the_temp_file()
    {
        using var dir = new TestDir();
        string final = dir.Write("final.bin", [7]);
        var oldId = _fs.TryGetIdentity(final)!.Value.FileId;
        string temp = dir.PathOf("~tqtemp.tmp");

        using (var file = _fs.CreateTemp(temp))
        {
            file.Write([1, 2], 0);
            file.Flush();
            file.RenameTo(final, replaceExisting: true);
        }

        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(final));
        Assert.NotEqual(oldId, _fs.TryGetIdentity(final)!.Value.FileId);
    }

    [Fact]
    public void DeleteIfSameObject_deletes_only_the_matching_object()
    {
        using var dir = new TestDir();
        string a = dir.Write("a.bin", [1]);
        string b = dir.Write("b.bin", [2]);
        var bId = _fs.TryGetIdentity(b)!.Value.FileId;

        Assert.False(_fs.DeleteIfSameObject(a, bId));
        Assert.True(File.Exists(a));

        Assert.True(_fs.DeleteIfSameObject(b, bId));
        Assert.False(File.Exists(b));

        Assert.False(_fs.DeleteIfSameObject(b, bId));
    }

    [Fact]
    public void DeleteIfSameObject_removes_read_only_files()
    {
        using var dir = new TestDir();
        string path = dir.Write("ro.bin", [1]);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var id = _fs.TryGetIdentity(path)!.Value.FileId;

        Assert.True(_fs.DeleteIfSameObject(path, id));
        Assert.False(File.Exists(path));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~TempFileTests`
Expected: FAIL with `NotImplementedException: Task 2`.

- [ ] **Step 3: Write the implementation**

In `WindowsFileSystem.cs`, add `using System.Runtime.InteropServices;` at the top. Replace the `CreateTemp` line with:

```csharp
    public ITempFile CreateTemp(string path)
    {
        // DELETE access is needed to rename through the handle. Share read only: verification may read it,
        // nobody else may write, rename or delete it while it's open.
        var handle = Open(path, Native.GENERIC_READ | Native.GENERIC_WRITE | Native.DELETE, Native.FILE_SHARE_READ,
            Native.CREATE_NEW, 0, "create temp file");
        return new WindowsTempFile(handle, path);
    }
```

Replace the `DeleteIfSameObject` line with:

```csharp
    public unsafe bool DeleteIfSameObject(string path, UInt128 fileId)
    {
        SafeFileHandle handle;
        try
        {
            handle = Open(path, Native.DELETE | Native.FILE_READ_ATTRIBUTES, Native.FILE_SHARE_ALL, Native.OPEN_EXISTING,
                Native.FILE_FLAG_OPEN_REPARSE_POINT, "open for cleanup");
        }
        catch (FsException ex) when (ex.IsNotFound)
        {
            return false;
        }

        using (handle)
        {
            if (Native.ReadIdentity(handle, path).FileId != fileId) return false;

            // Delete through the handle we just checked, so the delete applies to this object, not whatever is at the path.
            uint flags = Native.FILE_DISPOSITION_FLAG_DELETE | Native.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                       | Native.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
            if (Native.SetFileInformationByHandle(handle, Native.FileDispositionInfoEx, &flags, sizeof(uint))) return true;

            int error = Marshal.GetLastPInvokeError();
            if (error is not (Native.ERROR_INVALID_PARAMETER or Native.ERROR_NOT_SUPPORTED))
                throw new FsException("delete", path, error);

            byte delete = 1; // filesystems without FileDispositionInfoEx (FAT, exFAT)
            if (!Native.SetFileInformationByHandle(handle, Native.FileDispositionInfo, &delete, 1))
                throw Native.LastError("delete", path);
            return true;
        }
    }
```

At the end of the file, add:

```csharp
internal sealed unsafe class WindowsTempFile(SafeFileHandle handle, string path) : ITempFile
{
    public FileIdentity GetIdentity() => Native.ReadIdentity(handle, path);

    public void Preallocate(long size)
    {
        long value = size;
        Set(Native.FileAllocationInfo, &value, sizeof(long), "preallocate");
    }

    public void Write(ReadOnlySpan<byte> data, long offset) => RandomAccess.Write(handle, data, offset);

    public void SetLength(long length)
    {
        long value = length;
        Set(Native.FileEndOfFileInfo, &value, sizeof(long), "set the length of");
    }

    public void SetTimesAndAttributes(long creationTime, long lastWriteTime, uint attributes)
    {
        // Zero means "leave unchanged" for every field.
        var info = new Native.FILE_BASIC_INFO
        {
            CreationTime = creationTime,
            LastWriteTime = lastWriteTime,
            FileAttributes = attributes,
        };
        Set(Native.FileBasicInfo, &info, sizeof(Native.FILE_BASIC_INFO), "set the times of");
    }

    public void Flush()
    {
        if (!Native.FlushFileBuffers(handle)) throw Native.LastError("flush", path);
    }

    public void RenameTo(string finalPath, bool replaceExisting)
    {
        // FILE_RENAME_INFO on x64: ReplaceIfExists at 0, RootDirectory at 8, FileNameLength at 16, FileName at 20.
        string target = Native.LongPath(finalPath);
        int nameBytes = target.Length * sizeof(char);
        int size = 24 + nameBytes;
        byte* buffer = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            buffer[0] = replaceExisting ? (byte)1 : (byte)0;
            *(nint*)(buffer + 8) = 0;
            *(uint*)(buffer + 16) = (uint)nameBytes;
            fixed (char* name = target) Buffer.MemoryCopy(name, buffer + 20, nameBytes, nameBytes);
            if (!Native.SetFileInformationByHandle(handle, Native.FileRenameInfo, buffer, (uint)size))
                throw Native.LastError("publish", finalPath);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
        path = finalPath;
    }

    public void Dispose() => handle.Dispose();

    private void Set(int infoClass, void* info, int size, string operation)
    {
        if (!Native.SetFileInformationByHandle(handle, infoClass, info, (uint)size)) throw Native.LastError(operation, path);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 12 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: temp file writes, rename by handle and owned cleanup" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Unbuffered verification reads

**Files:**
- Create: `src/Squeue.Core/FileSystem/AlignedBuffer.cs`
- Modify: `src/Squeue.Core/FileSystem/WindowsFileSystem.cs` (replace `OpenForVerify`; add `WindowsVerifyFile`)
- Test: `tests/Squeue.Tests/FileSystem/VerifyReadTests.cs`

**Interfaces:**
- Consumes: `WindowsFileSystem.Open`, `WindowsFileSystem.CreateTemp` from Tasks 1–2.
- Produces: `sealed unsafe class AlignedBuffer : IDisposable` with `const int Alignment = 4096`, `AlignedBuffer(int length)` (throws `ArgumentException` unless length is a positive multiple of 4096), `int Length`, `Span<byte> Span`. Working `WindowsFileSystem.OpenForVerify(string) : IVerifyFile` (unbuffered, shares read/write/delete so it can read an open temp file).

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/FileSystem/VerifyReadTests.cs`:

```csharp
using Squeue.Core.FileSystem;

namespace Squeue.Tests.FileSystem;

public class VerifyReadTests
{
    private const int Chunk = 64 * 1024;
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void AlignedBuffer_rejects_unaligned_lengths()
    {
        Assert.Throws<ArgumentException>(() => new AlignedBuffer(1000));
        Assert.Throws<ArgumentException>(() => new AlignedBuffer(0));
        using var ok = new AlignedBuffer(Chunk);
        Assert.Equal(Chunk, ok.Span.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    public void Unbuffered_read_returns_exact_content(int size)
    {
        using var dir = new TestDir();
        var content = TestDir.RandomBytes(size);
        string path = dir.Write("a.bin", content);

        Assert.Equal(content, ReadAllUnbuffered(path));
    }

    [Fact]
    public void Can_verify_a_temp_file_while_its_handle_is_still_open()
    {
        using var dir = new TestDir();
        var content = TestDir.RandomBytes(Chunk + 10);
        string temp = dir.PathOf("~tqtemp.tmp");

        using var file = _fs.CreateTemp(temp);
        file.Write(content, 0);
        file.Flush();

        Assert.Equal(content, ReadAllUnbuffered(temp));
    }

    private byte[] ReadAllUnbuffered(string path)
    {
        using var file = _fs.OpenForVerify(path);
        using var buffer = new AlignedBuffer(Chunk);
        var result = new List<byte>();
        long offset = 0;
        while (true)
        {
            int read = file.Read(buffer.Span, offset);
            if (read == 0) break;
            result.AddRange(buffer.Span[..read].ToArray());
            offset += read;
            if (read < buffer.Length) break;
        }
        return result.ToArray();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~VerifyReadTests`
Expected: build FAILS — `AlignedBuffer` doesn't exist.

- [ ] **Step 3: Write the implementation**

`src/Squeue.Core/FileSystem/AlignedBuffer.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Squeue.Core.FileSystem;

/// Native memory aligned to 4096 bytes, as unbuffered (FILE_FLAG_NO_BUFFERING) reads require.
public sealed unsafe class AlignedBuffer : IDisposable
{
    public const int Alignment = 4096;
    private void* _memory;

    public AlignedBuffer(int length)
    {
        if (length <= 0 || length % Alignment != 0)
            throw new ArgumentException("Length must be a positive multiple of 4096.", nameof(length));
        _memory = NativeMemory.AlignedAlloc((nuint)length, Alignment);
        Length = length;
    }

    public int Length { get; }

    public Span<byte> Span => _memory == null
        ? throw new ObjectDisposedException(nameof(AlignedBuffer))
        : new Span<byte>(_memory, Length);

    public void Dispose()
    {
        if (_memory == null) return;
        NativeMemory.AlignedFree(_memory);
        _memory = null;
    }
}
```

In `WindowsFileSystem.cs`, replace the `OpenForVerify` line with:

```csharp
    public IVerifyFile OpenForVerify(string path)
    {
        // Share everything: verification may read a temp file whose write handle is still open.
        var handle = Open(path, Native.GENERIC_READ, Native.FILE_SHARE_ALL, Native.OPEN_EXISTING,
            Native.FILE_FLAG_NO_BUFFERING | Native.FILE_FLAG_SEQUENTIAL_SCAN, "open for verification");
        return new WindowsVerifyFile(handle);
    }
```

At the end of the file, add:

```csharp
internal sealed class WindowsVerifyFile(SafeFileHandle handle) : IVerifyFile
{
    public int Read(Span<byte> buffer, long offset) => RandomAccess.Read(handle, buffer, offset);
    public void Dispose() => handle.Dispose();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 23 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: unbuffered verification reads with aligned buffers" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Streaming hashers

**Files:**
- Create: `src/Squeue.Core/Copy/Hashers.cs`
- Test: `tests/Squeue.Tests/Copy/HasherTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `interface IContentHasher { void Append(ReadOnlySpan<byte> data); string FinishHex(); }` (lowercase hex) and `static class Hashers { static string[] Supported; static IContentHasher Create(string algorithm); }` — throws `ArgumentException` for unknown names.

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/Copy/HasherTests.cs`:

```csharp
using System.Text;
using Squeue.Core.Copy;

namespace Squeue.Tests.Copy;

public class HasherTests
{
    [Theory]
    [InlineData("xxh3", "", "2d06800538d394c2")]
    [InlineData("xxh128", "", "99aa06d3014798d86001c324468d497f")]
    [InlineData("crc32", "123456789", "cbf43926")]
    [InlineData("md5", "", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("sha1", "abc", "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData("sha256", "abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void Known_vectors(string algorithm, string input, string expected)
    {
        var hasher = Hashers.Create(algorithm);
        hasher.Append(Encoding.ASCII.GetBytes(input));
        Assert.Equal(expected, hasher.FinishHex());
    }

    [Theory]
    [MemberData(nameof(AllAlgorithms))]
    public void Chunked_input_hashes_the_same_as_one_block(string algorithm)
    {
        var data = TestDir.RandomBytes(1_000_003);
        var whole = Hashers.Create(algorithm);
        whole.Append(data);

        var chunked = Hashers.Create(algorithm);
        int offset = 0;
        foreach (int size in new[] { 1, 4095, 65536, 7, 300_000 })
        {
            chunked.Append(data.AsSpan(offset, size));
            offset += size;
        }
        chunked.Append(data.AsSpan(offset));

        Assert.Equal(whole.FinishHex(), chunked.FinishHex());
    }

    [Fact]
    public void Unknown_algorithm_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Hashers.Create("blake9"));

    public static IEnumerable<object[]> AllAlgorithms() => Hashers.Supported.Select(a => new object[] { a });
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~HasherTests`
Expected: build FAILS — `Hashers` doesn't exist.

- [ ] **Step 3: Write the implementation**

`src/Squeue.Core/Copy/Hashers.cs`:

```csharp
using System.IO.Hashing;
using System.Security.Cryptography;

namespace Squeue.Core.Copy;

public interface IContentHasher
{
    void Append(ReadOnlySpan<byte> data);

    /// Lowercase hex of the hash of everything appended.
    string FinishHex();
}

public static class Hashers
{
    public static readonly string[] Supported = ["xxh3", "xxh128", "crc32", "md5", "sha1", "sha256"];

    public static IContentHasher Create(string algorithm) => algorithm switch
    {
        "xxh3" => new Xxh3Hasher(),
        "xxh128" => new Xxh128Hasher(),
        "crc32" => new Crc32Hasher(),
        "md5" => new CryptoHasher(HashAlgorithmName.MD5),
        "sha1" => new CryptoHasher(HashAlgorithmName.SHA1),
        "sha256" => new CryptoHasher(HashAlgorithmName.SHA256),
        _ => throw new ArgumentException($"Unknown hash algorithm '{algorithm}'.", nameof(algorithm)),
    };

    private sealed class Xxh3Hasher : IContentHasher
    {
        private readonly XxHash3 _hash = new();
        public void Append(ReadOnlySpan<byte> data) => _hash.Append(data);
        public string FinishHex() => _hash.GetCurrentHashAsUInt64().ToString("x16");
    }

    private sealed class Xxh128Hasher : IContentHasher
    {
        private readonly XxHash128 _hash = new();
        public void Append(ReadOnlySpan<byte> data) => _hash.Append(data);
        public string FinishHex() => _hash.GetCurrentHashAsUInt128().ToString("x32");
    }

    private sealed class Crc32Hasher : IContentHasher
    {
        private readonly Crc32 _hash = new();
        public void Append(ReadOnlySpan<byte> data) => _hash.Append(data);
        public string FinishHex() => _hash.GetCurrentHashAsUInt32().ToString("x8");
    }

    private sealed class CryptoHasher(HashAlgorithmName name) : IContentHasher
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(name);
        public void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);
        public string FinishHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 36 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: streaming hashers (xxh3 default)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Temp file names

**Files:**
- Create: `src/Squeue.Core/Copy/TempNames.cs`
- Test: `tests/Squeue.Tests/Copy/TempNameTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static class TempNames { static string New(); static bool HasTempShape(string fileName); }`.

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/Copy/TempNameTests.cs`:

```csharp
using Squeue.Core.Copy;

namespace Squeue.Tests.Copy;

public class TempNameTests
{
    [Fact]
    public void Names_are_short_and_have_the_temp_shape()
    {
        string name = TempNames.New();
        Assert.Matches("^~tq[a-z2-7]{10}\\.tmp$", name);
        Assert.Equal(17, name.Length);
        Assert.True(TempNames.HasTempShape(name));
    }

    [Fact]
    public void Names_are_unique()
    {
        var names = Enumerable.Range(0, 10_000).Select(_ => TempNames.New()).ToHashSet();
        Assert.Equal(10_000, names.Count);
    }

    [Theory]
    [InlineData("photo.tmp")]
    [InlineData("~tqABCDEFGHIJ.tmp")]
    [InlineData("~tqabc.tmp")]
    public void Other_names_do_not_have_the_temp_shape(string name) =>
        Assert.False(TempNames.HasTempShape(name));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~TempNameTests`
Expected: build FAILS — `TempNames` doesn't exist.

- [ ] **Step 3: Write the implementation**

`src/Squeue.Core/Copy/TempNames.cs`:

```csharp
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Squeue.Core.Copy;

/// Short random names for temp files: "~tq" + 10 base32 characters + ".tmp" (17 characters).
/// Short enough for any filesystem's name-length limit; random so they never collide or tunnel.
public static partial class TempNames
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string New()
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        Span<char> chars = stackalloc char[10];
        for (int i = 0; i < chars.Length; i++) chars[i] = Alphabet[random[i] & 31];
        return string.Concat("~tq", chars, ".tmp");
    }

    /// Shape only. Ownership is always decided by the journal and the file id, never by the name.
    public static bool HasTempShape(string fileName) => Shape().IsMatch(fileName);

    [GeneratedRegex("^~tq[a-z2-7]{10}\\.tmp$")]
    private static partial Regex Shape();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 41 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: random temp file names" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: SQLite journal

**Files:**
- Create: `src/Squeue.Core/State/Records.cs`, `src/Squeue.Core/State/IdentityBlob.cs`, `src/Squeue.Core/State/Journal.cs`
- Test: `tests/Squeue.Tests/State/JournalTests.cs`

**Interfaces:**
- Consumes: `FileIdentity` from Task 1.
- Produces (namespace `Squeue.Core.State`):
  - `enum ConflictAction { Create, Replace }`
  - `enum EntryState { Pending, Active, Done, Failed }`
  - `enum AttemptPhase { Intent, TempCreated, Written, VerifiedTemp, Published, Verified, Finished, Abandoned }`
  - `sealed record Entry(long Id, long JobId, string SrcPath, string DestPath, long PlannedSize, ConflictAction Action, FileIdentity? SeenDest, EntryState State, string? HashAlgorithm, FileIdentity? SourceVersion, string? SrcHash, string? DestHash, int VerifyFailures, string? Error)` with `string DestDirectory`.
  - `sealed record Attempt(long Id, long EntryId, AttemptPhase Phase, string TempName, UInt128? TempFileId, UInt128? ReplacedFileId, UInt128? PublishedFileId, long StartedAt)` — `StartedAt` is FILETIME.
  - `sealed class Journal : IDisposable` with: `static Journal Open(string path)`, `long CreateJob(string? hashAlgorithm)`, `long AddEntry(long jobId, string srcPath, string destPath, long plannedSize, ConflictAction action, FileIdentity? seenDest)`, `Entry GetEntry(long entryId)`, `void RecordSourceVersion(long entryId, FileIdentity version)`, `void SetEntryState(long entryId, EntryState state, string? error = null)`, `void SetHashes(long entryId, string? srcHash, string? destHash)` (null leaves a value unchanged), `int RecordVerifyFailure(long entryId, FileIdentity? newReplaceTarget)` (returns the new failure count), `long BeginAttempt(long entryId, string tempName, UInt128? replacedFileId)`, `void SetPhase(long attemptId, AttemptPhase phase, UInt128? tempFileId = null, UInt128? publishedFileId = null)`, `Attempt? GetOpenAttempt(long entryId)`, `IReadOnlyList<Attempt> OpenAttempts()`, `IReadOnlyList<Attempt> AttemptsFor(long entryId)`, `string Pragma(string name)`.

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/State/JournalTests.cs`:

```csharp
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Tests.State;

public class JournalTests
{
    private static readonly FileIdentity Sample = new(7, (UInt128)123456789, 42, 1, 2, 3, 0x20);

    [Fact]
    public void Uses_wal_and_full_synchronous_mode()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        Assert.Equal("wal", journal.Pragma("journal_mode"));
        Assert.Equal("2", journal.Pragma("synchronous"));
    }

    [Fact]
    public void Entry_round_trips_with_identities_and_hashes()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long job = journal.CreateJob("xxh3");
        long id = journal.AddEntry(job, @"C:\src\a.bin", @"D:\dest\a.bin", 42, ConflictAction.Replace, Sample);

        journal.RecordSourceVersion(id, Sample with { Size = 43 });
        journal.SetHashes(id, "aa", null);
        journal.SetHashes(id, null, "bb");
        journal.SetEntryState(id, EntryState.Failed, "boom");
        var entry = journal.GetEntry(id);

        Assert.Equal(@"D:\dest", entry.DestDirectory);
        Assert.Equal(ConflictAction.Replace, entry.Action);
        Assert.Equal(Sample, entry.SeenDest);
        Assert.Equal(43, entry.SourceVersion!.Value.Size);
        Assert.Equal("xxh3", entry.HashAlgorithm);
        Assert.Equal(("aa", "bb"), (entry.SrcHash, entry.DestHash));
        Assert.Equal((EntryState.Failed, "boom"), (entry.State, entry.Error));
    }

    [Fact]
    public void Finished_and_abandoned_attempts_are_no_longer_open()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long entry = journal.AddEntry(journal.CreateJob(null), "a", "b", 1, ConflictAction.Create, null);

        long first = journal.BeginAttempt(entry, "~tqaaaaaaaaaa.tmp", null);
        journal.SetPhase(first, AttemptPhase.TempCreated, tempFileId: (UInt128)5);
        Assert.Equal(first, journal.GetOpenAttempt(entry)!.Id);
        Assert.Equal((UInt128)5, journal.GetOpenAttempt(entry)!.TempFileId);

        journal.SetPhase(first, AttemptPhase.Abandoned);
        long second = journal.BeginAttempt(entry, "~tqbbbbbbbbbb.tmp", (UInt128)9);
        journal.SetPhase(second, AttemptPhase.Published, publishedFileId: (UInt128)6);

        Assert.Single(journal.OpenAttempts());
        var open = journal.GetOpenAttempt(entry)!;
        Assert.Equal((second, AttemptPhase.Published), (open.Id, open.Phase));
        Assert.Equal(((UInt128)9, (UInt128)6), (open.ReplacedFileId!.Value, open.PublishedFileId!.Value));

        journal.SetPhase(second, AttemptPhase.Finished);
        Assert.Empty(journal.OpenAttempts());
        Assert.Equal(2, journal.AttemptsFor(entry).Count);
    }

    [Fact]
    public void State_survives_closing_and_reopening()
    {
        using var dir = new TestDir();
        string path = dir.PathOf("state.db");
        long entry;
        using (var journal = Journal.Open(path))
        {
            entry = journal.AddEntry(journal.CreateJob("xxh3"), "a", "b", 1, ConflictAction.Create, null);
            journal.BeginAttempt(entry, "~tqaaaaaaaaaa.tmp", null);
        }

        using var reopened = Journal.Open(path);
        Assert.Equal("a", reopened.GetEntry(entry).SrcPath);
        Assert.Single(reopened.OpenAttempts());
    }

    [Fact]
    public void RecordVerifyFailure_switches_to_replacing_the_published_file()
    {
        using var dir = new TestDir();
        using var journal = Journal.Open(dir.PathOf("state.db"));
        long id = journal.AddEntry(journal.CreateJob("xxh3"), "a", "b", 1, ConflictAction.Create, null);
        journal.SetHashes(id, "aa", "bb");

        Assert.Equal(1, journal.RecordVerifyFailure(id, Sample));
        var entry = journal.GetEntry(id);
        Assert.Equal(ConflictAction.Replace, entry.Action);
        Assert.Equal(Sample, entry.SeenDest);
        Assert.Null(entry.SrcHash);
        Assert.Null(entry.DestHash);

        Assert.Equal(2, journal.RecordVerifyFailure(id, null));
        Assert.Equal(Sample, journal.GetEntry(id).SeenDest);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~JournalTests`
Expected: build FAILS — `Squeue.Core.State` doesn't exist.

- [ ] **Step 3: Write the implementation**

`src/Squeue.Core/State/Records.cs`:

```csharp
using Squeue.Core.FileSystem;

namespace Squeue.Core.State;

public enum ConflictAction { Create, Replace }

public enum EntryState { Pending, Active, Done, Failed }

/// Journal phases of one attempt, in protocol order. Finished and Abandoned are terminal.
public enum AttemptPhase { Intent, TempCreated, Written, VerifiedTemp, Published, Verified, Finished, Abandoned }

public sealed record Entry(
    long Id,
    long JobId,
    string SrcPath,
    string DestPath,
    long PlannedSize,
    ConflictAction Action,
    FileIdentity? SeenDest,
    EntryState State,
    string? HashAlgorithm,
    FileIdentity? SourceVersion,
    string? SrcHash,
    string? DestHash,
    int VerifyFailures,
    string? Error)
{
    public string DestDirectory => Path.GetDirectoryName(DestPath)!;
}

public sealed record Attempt(
    long Id,
    long EntryId,
    AttemptPhase Phase,
    string TempName,
    UInt128? TempFileId,
    UInt128? ReplacedFileId,
    UInt128? PublishedFileId,
    long StartedAt);
```

`src/Squeue.Core/State/IdentityBlob.cs`:

```csharp
using System.Buffers.Binary;
using Squeue.Core.FileSystem;

namespace Squeue.Core.State;

/// Stores a FileIdentity as 60 bytes: volume, file id, size, creation, last write, change, attributes.
internal static class IdentityBlob
{
    public static byte[]? Encode(FileIdentity? identity)
    {
        if (identity is not { } id) return null;
        var bytes = new byte[60];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0), id.VolumeSerial);
        BinaryPrimitives.WriteUInt128LittleEndian(bytes.AsSpan(8), id.FileId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), id.Size);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), id.CreationTime);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), id.LastWriteTime);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48), id.ChangeTime);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), id.Attributes);
        return bytes;
    }

    public static FileIdentity? Decode(byte[]? bytes) => bytes is null
        ? null
        : new FileIdentity(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0)),
            BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(56)));
}
```

`src/Squeue.Core/State/Journal.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Squeue.Core.FileSystem;

namespace Squeue.Core.State;

/// The operation journal. Every method commits before it returns (synchronous = FULL),
/// so anything the copier does after a call can rely on that state surviving a crash.
public sealed class Journal : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS jobs (
          id         INTEGER PRIMARY KEY,
          hash_algo  TEXT,
          created_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS entries (
          id              INTEGER PRIMARY KEY,
          job_id          INTEGER NOT NULL REFERENCES jobs(id),
          src_path        TEXT NOT NULL,
          dest_path       TEXT NOT NULL,
          planned_size    INTEGER NOT NULL,
          conflict_action TEXT NOT NULL,
          seen_dest       BLOB,
          src_version     BLOB,
          state           TEXT NOT NULL,
          src_hash        TEXT,
          dest_hash       TEXT,
          verify_failures INTEGER NOT NULL DEFAULT 0,
          error           TEXT
        );
        CREATE TABLE IF NOT EXISTS attempts (
          id                INTEGER PRIMARY KEY,
          entry_id          INTEGER NOT NULL REFERENCES entries(id),
          phase             TEXT NOT NULL,
          temp_name         TEXT NOT NULL,
          temp_file_id      BLOB,
          replaced_file_id  BLOB,
          published_file_id BLOB,
          started_at        INTEGER NOT NULL,
          ended_at          INTEGER
        );
        CREATE INDEX IF NOT EXISTS attempts_open ON attempts(phase) WHERE phase NOT IN ('Finished', 'Abandoned');
        """;

    private readonly SqliteConnection _db;

    private Journal(SqliteConnection db) => _db = db;

    public static Journal Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        var journal = new Journal(db);
        journal.Exec("PRAGMA journal_mode = WAL;");
        journal.Exec("PRAGMA synchronous = FULL;");
        journal.Exec("PRAGMA foreign_keys = ON;");
        journal.Exec(Schema);
        return journal;
    }

    public long CreateJob(string? hashAlgorithm) =>
        Insert("INSERT INTO jobs (hash_algo, created_at) VALUES ($hash, $now)", ("$hash", hashAlgorithm), ("$now", Now()));

    public long AddEntry(long jobId, string srcPath, string destPath, long plannedSize, ConflictAction action, FileIdentity? seenDest) =>
        Insert("""
            INSERT INTO entries (job_id, src_path, dest_path, planned_size, conflict_action, seen_dest, state)
            VALUES ($job, $src, $dest, $size, $action, $seen, 'Pending')
            """,
            ("$job", jobId), ("$src", srcPath), ("$dest", destPath), ("$size", plannedSize),
            ("$action", action.ToString()), ("$seen", IdentityBlob.Encode(seenDest)));

    public Entry GetEntry(long entryId)
    {
        using var cmd = Command("""
            SELECT e.id, e.job_id, e.src_path, e.dest_path, e.planned_size, e.conflict_action, e.seen_dest, e.state,
                   j.hash_algo, e.src_version, e.src_hash, e.dest_hash, e.verify_failures, e.error
            FROM entries e JOIN jobs j ON j.id = e.job_id
            WHERE e.id = $id
            """, ("$id", entryId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException($"No entry {entryId}.");
        return new Entry(
            r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
            Enum.Parse<ConflictAction>(r.GetString(5)), IdentityBlob.Decode(Blob(r, 6)), Enum.Parse<EntryState>(r.GetString(7)),
            Text(r, 8), IdentityBlob.Decode(Blob(r, 9)), Text(r, 10), Text(r, 11), r.GetInt32(12), Text(r, 13));
    }

    public void RecordSourceVersion(long entryId, FileIdentity version) =>
        Exec("UPDATE entries SET src_version = $v WHERE id = $id", ("$v", IdentityBlob.Encode(version)), ("$id", entryId));

    public void SetEntryState(long entryId, EntryState state, string? error = null) =>
        Exec("UPDATE entries SET state = $s, error = $e WHERE id = $id", ("$s", state.ToString()), ("$e", error), ("$id", entryId));

    public void SetHashes(long entryId, string? srcHash, string? destHash) =>
        Exec("UPDATE entries SET src_hash = COALESCE($src, src_hash), dest_hash = COALESCE($dest, dest_hash) WHERE id = $id",
            ("$src", srcHash), ("$dest", destHash), ("$id", entryId));

    /// Counts a verification failure and clears both hashes. With a target, the next attempt replaces that
    /// (already published, but unverified) file instead of creating a new one.
    public int RecordVerifyFailure(long entryId, FileIdentity? newReplaceTarget)
    {
        Exec("""
            UPDATE entries
            SET verify_failures = verify_failures + 1, src_hash = NULL, dest_hash = NULL,
                conflict_action = CASE WHEN $target IS NULL THEN conflict_action ELSE 'Replace' END,
                seen_dest = COALESCE($target, seen_dest)
            WHERE id = $id
            """, ("$target", IdentityBlob.Encode(newReplaceTarget)), ("$id", entryId));
        return GetEntry(entryId).VerifyFailures;
    }

    public long BeginAttempt(long entryId, string tempName, UInt128? replacedFileId) =>
        Insert("""
            INSERT INTO attempts (entry_id, phase, temp_name, replaced_file_id, started_at)
            VALUES ($entry, 'Intent', $temp, $replaced, $now)
            """, ("$entry", entryId), ("$temp", tempName), ("$replaced", IdBytes(replacedFileId)), ("$now", Now()));

    public void SetPhase(long attemptId, AttemptPhase phase, UInt128? tempFileId = null, UInt128? publishedFileId = null) =>
        Exec("""
            UPDATE attempts
            SET phase = $phase,
                temp_file_id = COALESCE($temp, temp_file_id),
                published_file_id = COALESCE($pub, published_file_id),
                ended_at = CASE WHEN $phase IN ('Finished', 'Abandoned') THEN $now ELSE ended_at END
            WHERE id = $id
            """, ("$phase", phase.ToString()), ("$temp", IdBytes(tempFileId)), ("$pub", IdBytes(publishedFileId)),
            ("$now", Now()), ("$id", attemptId));

    public Attempt? GetOpenAttempt(long entryId) =>
        ReadAttempts("WHERE entry_id = $entry AND phase NOT IN ('Finished', 'Abandoned') ORDER BY id DESC LIMIT 1",
            ("$entry", entryId)).FirstOrDefault();

    public IReadOnlyList<Attempt> OpenAttempts() =>
        ReadAttempts("WHERE phase NOT IN ('Finished', 'Abandoned') ORDER BY id");

    public IReadOnlyList<Attempt> AttemptsFor(long entryId) =>
        ReadAttempts("WHERE entry_id = $entry ORDER BY id", ("$entry", entryId));

    public string Pragma(string name)
    {
        using var cmd = Command($"PRAGMA {name}");
        return Convert.ToString(cmd.ExecuteScalar())!;
    }

    public void Dispose() => _db.Dispose();

    private List<Attempt> ReadAttempts(string where, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(
            "SELECT id, entry_id, phase, temp_name, temp_file_id, replaced_file_id, published_file_id, started_at FROM attempts " + where,
            args);
        using var r = cmd.ExecuteReader();
        var attempts = new List<Attempt>();
        while (r.Read())
        {
            attempts.Add(new Attempt(r.GetInt64(0), r.GetInt64(1), Enum.Parse<AttemptPhase>(r.GetString(2)), r.GetString(3),
                Id(Blob(r, 4)), Id(Blob(r, 5)), Id(Blob(r, 6)), r.GetInt64(7)));
        }
        return attempts;
    }

    private static long Now() => DateTime.UtcNow.ToFileTimeUtc();
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static byte[]? Blob(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : (byte[])r.GetValue(i);
    private static UInt128? Id(byte[]? bytes) => bytes is null ? null : FileIdentity.FileIdFromBytes(bytes);
    private static byte[]? IdBytes(UInt128? id) => id is null ? null : FileIdentity.FileIdToBytes(id.Value);

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }

    private long Insert(string sql, params (string Name, object? Value)[] args)
    {
        Exec(sql, args);
        using var cmd = Command("SELECT last_insert_rowid()");
        return (long)cmd.ExecuteScalar()!;
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 46 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: SQLite operation journal" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Fault-injecting file system for tests

**Files:**
- Create: `tests/Squeue.Tests/Fakes/FaultyFileSystem.cs`
- Test: `tests/Squeue.Tests/Fakes/FaultyFileSystemTests.cs`

**Interfaces:**
- Consumes: `IFileSystem` and handle interfaces from Task 1; `WindowsFileSystem` from Tasks 1–3.
- Produces (namespace `Squeue.Tests.Fakes`):
  - `enum FsOp { OpenSource, CreateTemp, Preallocate, Write, SetLength, SetTimes, Flush, Rename, OpenForVerify, VerifyRead, TryGetIdentity, Delete }`
  - `sealed class SimulatedCrashException : Exception` — deliberately **not** an `IOException`, so the copier's error handling never catches it (a real crash runs no handlers).
  - `sealed class FaultyFileSystem(IFileSystem inner) : IFileSystem` with `(FsOp Op, bool After, int Occurrence)? CrashAt { get; set; }` (fires once, then clears), `bool CorruptVerifyReads { get; set; }` (flips the first byte of every verify read), `bool Crashed { get; }`. When a crash fires after an open or create, the handle it produced is closed, as process death would.

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/Fakes/FaultyFileSystemTests.cs`:

```csharp
using Squeue.Core.FileSystem;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Fakes;

public class FaultyFileSystemTests
{
    [Fact]
    public void Crash_before_create_leaves_nothing()
    {
        using var dir = new TestDir();
        var fs = new FaultyFileSystem(new WindowsFileSystem()) { CrashAt = (FsOp.CreateTemp, false, 1) };

        Assert.Throws<SimulatedCrashException>(() => fs.CreateTemp(dir.PathOf("~tqaaaaaaaaaa.tmp")));

        Assert.True(fs.Crashed);
        Assert.False(File.Exists(dir.PathOf("~tqaaaaaaaaaa.tmp")));
    }

    [Fact]
    public void Crash_after_create_leaves_the_file_with_its_handle_closed()
    {
        using var dir = new TestDir();
        string path = dir.PathOf("~tqaaaaaaaaaa.tmp");
        var fs = new FaultyFileSystem(new WindowsFileSystem()) { CrashAt = (FsOp.CreateTemp, true, 1) };

        Assert.Throws<SimulatedCrashException>(() => fs.CreateTemp(path));

        Assert.True(File.Exists(path));
        File.Delete(path); // would fail with a sharing violation if the handle were still open
    }

    [Fact]
    public void Crash_fires_on_the_requested_occurrence_only_once()
    {
        using var dir = new TestDir();
        var fs = new FaultyFileSystem(new WindowsFileSystem()) { CrashAt = (FsOp.Write, false, 2) };
        using var temp = fs.CreateTemp(dir.PathOf("~tqaaaaaaaaaa.tmp"));

        temp.Write([1], 0);
        Assert.Throws<SimulatedCrashException>(() => temp.Write([2], 1));
        temp.Write([3], 1);
    }

    [Fact]
    public void CorruptVerifyReads_flips_the_first_byte()
    {
        using var dir = new TestDir();
        string path = dir.Write("a.bin", [10, 20, 30]);
        var fs = new FaultyFileSystem(new WindowsFileSystem()) { CorruptVerifyReads = true };

        using var file = fs.OpenForVerify(path);
        using var buffer = new AlignedBuffer(4096);
        int read = file.Read(buffer.Span, 0);

        Assert.Equal(3, read);
        Assert.Equal(new byte[] { (byte)(10 ^ 0xFF), 20, 30 }, buffer.Span[..3].ToArray());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~FaultyFileSystemTests`
Expected: build FAILS — `FaultyFileSystem` doesn't exist.

- [ ] **Step 3: Write the implementation**

`tests/Squeue.Tests/Fakes/FaultyFileSystem.cs`:

```csharp
using Squeue.Core.FileSystem;

namespace Squeue.Tests.Fakes;

public enum FsOp { OpenSource, CreateTemp, Preallocate, Write, SetLength, SetTimes, Flush, Rename, OpenForVerify, VerifyRead, TryGetIdentity, Delete }

/// Stands in for the process dying. Not an IOException, so no error handler in the copier catches it.
public sealed class SimulatedCrashException(string point) : Exception($"Simulated crash {point}");

/// Wraps a real file system and throws SimulatedCrashException just before or just after a chosen operation.
public sealed class FaultyFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly Dictionary<FsOp, int> _counts = [];

    public (FsOp Op, bool After, int Occurrence)? CrashAt { get; set; }
    public bool CorruptVerifyReads { get; set; }
    public bool Crashed { get; private set; }

    public ISourceFile OpenSource(string path) => Open(FsOp.OpenSource, () => new Source(inner.OpenSource(path)));
    public ITempFile CreateTemp(string path) => Open(FsOp.CreateTemp, () => new Temp(inner.CreateTemp(path), this));
    public IVerifyFile OpenForVerify(string path) => Open(FsOp.OpenForVerify, () => new Verify(inner.OpenForVerify(path), this));

    public FileIdentity? TryGetIdentity(string path)
    {
        Hit(FsOp.TryGetIdentity, after: false);
        var result = inner.TryGetIdentity(path);
        Hit(FsOp.TryGetIdentity, after: true);
        return result;
    }

    public bool DeleteIfSameObject(string path, UInt128 fileId)
    {
        Hit(FsOp.Delete, after: false);
        bool result = inner.DeleteIfSameObject(path, fileId);
        Hit(FsOp.Delete, after: true);
        return result;
    }

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    internal void Hit(FsOp op, bool after)
    {
        if (!after) _counts[op] = _counts.GetValueOrDefault(op) + 1;
        if (CrashAt is { } crash && crash.Op == op && crash.After == after && _counts.GetValueOrDefault(op) == crash.Occurrence)
        {
            CrashAt = null;
            Crashed = true;
            throw new SimulatedCrashException($"{(after ? "after" : "before")} {op} #{crash.Occurrence}");
        }
    }

    private T Open<T>(FsOp op, Func<T> open) where T : IDisposable
    {
        Hit(op, after: false);
        T handle = open();
        try { Hit(op, after: true); }
        catch { handle.Dispose(); throw; } // a dying process closes its handles
        return handle;
    }

    private sealed class Source(ISourceFile inner) : ISourceFile
    {
        public FileIdentity Identity => inner.Identity;
        public int Read(Span<byte> buffer, long offset) => inner.Read(buffer, offset);
        public void Dispose() => inner.Dispose();
    }

    private sealed class Temp(ITempFile inner, FaultyFileSystem fs) : ITempFile
    {
        public FileIdentity GetIdentity() => inner.GetIdentity();
        public void Preallocate(long size) { fs.Hit(FsOp.Preallocate, false); inner.Preallocate(size); fs.Hit(FsOp.Preallocate, true); }
        public void Write(ReadOnlySpan<byte> data, long offset) { fs.Hit(FsOp.Write, false); inner.Write(data, offset); fs.Hit(FsOp.Write, true); }
        public void SetLength(long length) { fs.Hit(FsOp.SetLength, false); inner.SetLength(length); fs.Hit(FsOp.SetLength, true); }

        public void SetTimesAndAttributes(long creationTime, long lastWriteTime, uint attributes)
        {
            fs.Hit(FsOp.SetTimes, false);
            inner.SetTimesAndAttributes(creationTime, lastWriteTime, attributes);
            fs.Hit(FsOp.SetTimes, true);
        }

        public void Flush() { fs.Hit(FsOp.Flush, false); inner.Flush(); fs.Hit(FsOp.Flush, true); }

        public void RenameTo(string finalPath, bool replaceExisting)
        {
            fs.Hit(FsOp.Rename, false);
            inner.RenameTo(finalPath, replaceExisting);
            fs.Hit(FsOp.Rename, true);
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class Verify(IVerifyFile inner, FaultyFileSystem fs) : IVerifyFile
    {
        public int Read(Span<byte> buffer, long offset)
        {
            fs.Hit(FsOp.VerifyRead, false);
            int read = inner.Read(buffer, offset);
            if (fs.CorruptVerifyReads && read > 0) buffer[0] ^= 0xFF;
            fs.Hit(FsOp.VerifyRead, true);
            return read;
        }

        public void Dispose() => inner.Dispose();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 50 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "test: fault-injecting file system" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: The per-file copier

**Files:**
- Create: `src/Squeue.Core/Copy/CopyTypes.cs`, `src/Squeue.Core/Copy/FileCopier.cs`
- Create: `tests/Squeue.Tests/CopyScenario.cs`
- Test: `tests/Squeue.Tests/Copy/FileCopierTests.cs`

**Interfaces:**
- Consumes: `IFileSystem` & handles (Task 1), `AlignedBuffer` (Task 3), `Hashers` (Task 4), `TempNames` (Task 5), `Journal` + records (Task 6), `FaultyFileSystem` (Task 7, tests only).
- Produces:
  - `sealed record CopyOptions { int ChunkSize = 4 MiB; TimeSpan SharingRetryTimeout = 10 s; TimeSpan SharingRetryDelay = 250 ms; }`
  - `enum CopyOutcome { Done, Failed, DestinationChanged, RetryNeeded }`, `sealed record CopyResult(CopyOutcome Outcome, string? Message = null)`
  - `sealed class FileCopier(IFileSystem fs, Journal journal, CopyOptions? options = null)` with `CopyResult CopyEntry(long entryId)`. `CopyEntry` returns `Done` immediately for a `Done` entry; resumes an open `Published`/`Verified` attempt; throws `InvalidOperationException` if an earlier open attempt exists (the Reconciler must run first).
  - Test helper `sealed class CopyScenario : IDisposable` with `TestDir Dir`, `Journal Journal`, `string Src`, `string Dest`, `string DestDir`, `long AddEntry(string? hash = "xxh3", ConflictAction action = Create, FileIdentity? seenDest = null)`, `void ReopenJournal()`, `string[] TempFiles()`.

- [ ] **Step 1: Write the test helper**

`tests/Squeue.Tests/CopyScenario.cs`:

```csharp
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Tests;

/// A test folder with a journal and one source/destination pair.
public sealed class CopyScenario : IDisposable
{
    private readonly string _dbPath;

    public CopyScenario(string srcName = @"src\a.bin", string destName = @"dest\a.bin")
    {
        _dbPath = Dir.PathOf("state.db");
        Journal = Journal.Open(_dbPath);
        Src = Dir.PathOf(srcName);
        Dest = Dir.PathOf(destName);
    }

    public TestDir Dir { get; } = new();
    public Journal Journal { get; private set; }
    public string Src { get; }
    public string Dest { get; }
    public string DestDir => Path.GetDirectoryName(Dest)!;

    public void WriteSource(byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Src)!);
        File.WriteAllBytes(Src, content);
    }

    public void WriteDest(byte[] content)
    {
        Directory.CreateDirectory(DestDir);
        File.WriteAllBytes(Dest, content);
    }

    public long AddEntry(string? hash = "xxh3", ConflictAction action = ConflictAction.Create, FileIdentity? seenDest = null) =>
        Journal.AddEntry(Journal.CreateJob(hash), Src, Dest, new FileInfo(Src).Length, action, seenDest);

    /// Simulates an app restart: a fresh database connection.
    public void ReopenJournal()
    {
        Journal.Dispose();
        Journal = Journal.Open(_dbPath);
    }

    public string[] TempFiles() => Directory.Exists(DestDir) ? Directory.GetFiles(DestDir, "~tq*.tmp") : [];

    public void Dispose()
    {
        Journal.Dispose();
        Dir.Dispose();
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Squeue.Tests/Copy/FileCopierTests.cs`:

```csharp
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Copy;

public class FileCopierTests
{
    private const int Chunk = 64 * 1024;
    private static readonly CopyOptions Options = new() { ChunkSize = Chunk, SharingRetryTimeout = TimeSpan.Zero };
    private readonly WindowsFileSystem _fs = new();

    [Fact]
    public void Copies_a_new_file_and_verifies_it()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(3 * Chunk + 17);
        s.WriteSource(content);
        long id = s.AddEntry();

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id);

        Assert.Equal(CopyOutcome.Done, result.Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Equal(File.GetLastWriteTimeUtc(s.Src), File.GetLastWriteTimeUtc(s.Dest));
        var entry = s.Journal.GetEntry(id);
        Assert.Equal(EntryState.Done, entry.State);
        Assert.NotNull(entry.SrcHash);
        Assert.Equal(entry.SrcHash, entry.DestHash);
        Assert.Empty(s.TempFiles());
        Assert.Empty(s.Journal.OpenAttempts());
    }

    [Fact]
    public void Copies_without_verification_when_no_hash_is_set()
    {
        using var s = new CopyScenario();
        s.WriteSource([1, 2, 3]);
        long id = s.AddEntry(hash: null);

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(s.Dest));
        Assert.Null(s.Journal.GetEntry(id).DestHash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk)]
    public void Copies_edge_sizes(int size)
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(size);
        s.WriteSource(content);
        long id = s.AddEntry();

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
    }

    [Fact]
    public void Copies_long_paths_with_unicode_names()
    {
        string deep = string.Join('\\', Enumerable.Repeat("a rather long folder name", 12));
        using var s = new CopyScenario(srcName: $@"src\{deep}\Café 写真 📷.NEF", destName: $@"dest\{deep}\Café 写真 📷.NEF");
        Assert.True(s.Dest.Length > 300);
        var content = TestDir.RandomBytes(Chunk + 5);
        s.WriteSource(content);
        long id = s.AddEntry();

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
    }

    [Fact]
    public void Replace_verifies_first_then_replaces_the_existing_file()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(Chunk + 1);
        s.WriteSource(content);
        s.WriteDest([7, 7, 7]);
        var seen = _fs.TryGetIdentity(s.Dest)!.Value;
        long id = s.AddEntry(action: ConflictAction.Replace, seenDest: seen);

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);

        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.NotEqual(seen.FileId, _fs.TryGetIdentity(s.Dest)!.Value.FileId);
        var attempt = s.Journal.AttemptsFor(id).Single();
        Assert.Equal(seen.FileId, attempt.ReplacedFileId);
        Assert.Empty(s.TempFiles());
    }

    [Fact]
    public void Replace_keeps_a_destination_that_changed_after_the_decision()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(100));
        s.WriteDest([7, 7, 7]);
        long id = s.AddEntry(action: ConflictAction.Replace, seenDest: _fs.TryGetIdentity(s.Dest));
        Thread.Sleep(50);
        s.WriteDest([8, 8, 8, 8]); // someone edits it before we publish

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id);

        Assert.Equal(CopyOutcome.DestinationChanged, result.Outcome);
        Assert.Equal(new byte[] { 8, 8, 8, 8 }, File.ReadAllBytes(s.Dest));
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
        Assert.Empty(s.TempFiles());
    }

    [Fact]
    public void Create_keeps_a_destination_that_appeared_after_planning()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(100));
        long id = s.AddEntry();
        s.WriteDest([9]);

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id);

        Assert.Equal(CopyOutcome.DestinationChanged, result.Outcome);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(s.Dest));
        Assert.Empty(s.TempFiles());
    }

    [Fact]
    public void Source_in_use_by_another_program_fails_with_reason()
    {
        using var s = new CopyScenario();
        s.WriteSource([1, 2, 3]);
        long id = s.AddEntry();
        using var writer = new FileStream(s.Src, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id);

        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        Assert.Equal("The source is in use by another program.", result.Message);
        Assert.False(File.Exists(s.Dest));
        Assert.Empty(s.TempFiles());
        Assert.Empty(s.Journal.AttemptsFor(id));
    }

    [Fact]
    public void Missing_source_fails_with_reason()
    {
        using var s = new CopyScenario();
        s.WriteSource([1]);
        long id = s.AddEntry();
        File.Delete(s.Src);

        var result = new FileCopier(_fs, s.Journal, Options).CopyEntry(id);

        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        Assert.Equal("The source no longer exists.", result.Message);
    }

    [Fact]
    public void A_done_entry_is_not_copied_again()
    {
        using var s = new CopyScenario();
        s.WriteSource([1]);
        long id = s.AddEntry();
        var copier = new FileCopier(_fs, s.Journal, Options);
        copier.CopyEntry(id);

        Assert.Equal(CopyOutcome.Done, copier.CopyEntry(id).Outcome);
        Assert.Single(s.Journal.AttemptsFor(id));
    }

    [Fact]
    public void One_verification_failure_recopies_and_succeeds()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(Chunk * 2);
        s.WriteSource(content);
        long id = s.AddEntry();
        var faulty = new FaultyFileSystem(_fs) { CorruptVerifyReads = true };
        var copier = new FileCopier(faulty, s.Journal, Options);

        Assert.Equal(CopyOutcome.RetryNeeded, copier.CopyEntry(id).Outcome);
        Assert.Equal(ConflictAction.Replace, s.Journal.GetEntry(id).Action);

        faulty.CorruptVerifyReads = false;
        Assert.Equal(CopyOutcome.Done, copier.CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Empty(s.TempFiles());
    }

    [Fact]
    public void Two_verification_failures_fail_the_entry_and_keep_the_source()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(Chunk));
        long id = s.AddEntry();
        var copier = new FileCopier(new FaultyFileSystem(_fs) { CorruptVerifyReads = true }, s.Journal, Options);

        Assert.Equal(CopyOutcome.RetryNeeded, copier.CopyEntry(id).Outcome);
        var second = copier.CopyEntry(id);

        Assert.Equal(CopyOutcome.Failed, second.Outcome);
        Assert.Contains("twice", second.Message);
        Assert.True(File.Exists(s.Src));
        Assert.Empty(s.TempFiles());
        Assert.Equal(EntryState.Failed, s.Journal.GetEntry(id).State);
    }

    [Fact]
    public void Rejects_unaligned_chunk_sizes() =>
        Assert.Throws<ArgumentException>(() => new FileCopier(_fs, null!, new CopyOptions { ChunkSize = 1000 }));
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~FileCopierTests`
Expected: build FAILS — `FileCopier`, `CopyOptions` don't exist.

- [ ] **Step 4: Write the implementation**

`src/Squeue.Core/Copy/CopyTypes.cs`:

```csharp
namespace Squeue.Core.Copy;

public sealed record CopyOptions
{
    /// Bytes per read/write. Must be a positive multiple of 4096.
    public int ChunkSize { get; init; } = 4 * 1024 * 1024;

    /// How long to keep retrying when another program has the source open for writing.
    public TimeSpan SharingRetryTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan SharingRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}

public enum CopyOutcome
{
    Done,
    Failed,

    /// The destination isn't what the conflict decision was made against. Nothing was replaced.
    DestinationChanged,

    /// Verification failed once; the entry is pending and will be copied again.
    RetryNeeded,
}

public sealed record CopyResult(CopyOutcome Outcome, string? Message = null);
```

`src/Squeue.Core/Copy/FileCopier.cs`:

```csharp
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Copy;

/// Copies one journal entry following the per-file protocol:
/// intent → temp created → written + flushed → (verified, when replacing) → published + flushed → verified → finished.
/// Each phase is committed to the journal before the next filesystem change, so the Reconciler can always
/// tell what happened after a crash.
public sealed class FileCopier
{
    // Read-only, hidden, system, archive, not-content-indexed. Compression, encryption and sparse are not copied.
    private const uint CopiedAttributes = 0x1 | 0x2 | 0x4 | 0x20 | 0x2000;

    private readonly IFileSystem _fs;
    private readonly Journal _journal;
    private readonly CopyOptions _options;

    public FileCopier(IFileSystem fs, Journal journal, CopyOptions? options = null)
    {
        _options = options ?? new CopyOptions();
        if (_options.ChunkSize <= 0 || _options.ChunkSize % AlignedBuffer.Alignment != 0)
            throw new ArgumentException("Chunk size must be a positive multiple of 4096.", nameof(options));
        _fs = fs;
        _journal = journal;
    }

    public CopyResult CopyEntry(long entryId)
    {
        var entry = _journal.GetEntry(entryId);
        if (entry.State == EntryState.Done) return new CopyResult(CopyOutcome.Done);

        var open = _journal.GetOpenAttempt(entryId);
        if (open is { Phase: AttemptPhase.Published or AttemptPhase.Verified }) return FinishPublished(entry, open);
        if (open is not null)
            throw new InvalidOperationException($"Entry {entryId} has an unreconciled attempt. Run the Reconciler first.");

        return StartAttempt(entry);
    }

    private CopyResult StartAttempt(Entry entry)
    {
        ISourceFile source;
        try { source = OpenSourceWithRetry(entry.SrcPath); }
        catch (FsException ex) when (ex.IsSharingViolation) { return Fail(entry, "The source is in use by another program."); }
        catch (FsException ex) when (ex.IsNotFound) { return Fail(entry, "The source no longer exists."); }
        catch (IOException ex) { return Fail(entry, ex.Message); }

        using (source)
        {
            _journal.RecordSourceVersion(entry.Id, source.Identity);
            try { _fs.CreateDirectory(entry.DestDirectory); }
            catch (IOException ex) { return Fail(entry, ex.Message); }
            catch (UnauthorizedAccessException ex) { return Fail(entry, ex.Message); }

            string tempName = TempNames.New();
            string tempPath = Path.Combine(entry.DestDirectory, tempName);
            UInt128? replaces = entry.Action == ConflictAction.Replace ? entry.SeenDest?.FileId : null;
            long attemptId = _journal.BeginAttempt(entry.Id, tempName, replaces); // intent is durable before the file exists
            _journal.SetEntryState(entry.Id, EntryState.Active);

            ITempFile temp;
            try { temp = _fs.CreateTemp(tempPath); }
            catch (IOException ex)
            {
                _journal.SetPhase(attemptId, AttemptPhase.Abandoned);
                return Fail(entry, ex.Message);
            }

            using (temp)
            {
                UInt128 tempId = temp.GetIdentity().FileId;
                _journal.SetPhase(attemptId, AttemptPhase.TempCreated, tempFileId: tempId);
                try
                {
                    string? srcHash = WriteTemp(source, temp, entry.HashAlgorithm);
                    _journal.SetHashes(entry.Id, srcHash, null);
                    _journal.SetPhase(attemptId, AttemptPhase.Written);
                    source.Dispose(); // fully read; release it before publishing

                    if (entry.Action == ConflictAction.Replace)
                    {
                        // Never replace an existing file with one that hasn't passed verification.
                        if (srcHash is not null)
                        {
                            string tempHash = HashUnbuffered(tempPath, entry.HashAlgorithm!);
                            if (tempHash != srcHash)
                            {
                                Abandon(temp, tempPath, tempId, attemptId);
                                return VerificationFailed(entry, replaceTarget: null);
                            }
                            _journal.SetHashes(entry.Id, null, tempHash);
                            _journal.SetPhase(attemptId, AttemptPhase.VerifiedTemp);
                        }

                        if (_fs.TryGetIdentity(entry.DestPath) is not { } current
                            || entry.SeenDest is not { } seen
                            || !current.IsSameVersion(seen))
                        {
                            return DestinationChanged(entry, temp, tempPath, tempId, attemptId);
                        }
                    }

                    try { temp.RenameTo(entry.DestPath, replaceExisting: entry.Action == ConflictAction.Replace); }
                    catch (FsException ex) when (ex.IsAlreadyExists) { return DestinationChanged(entry, temp, tempPath, tempId, attemptId); }
                }
                catch (IOException ex)
                {
                    Abandon(temp, tempPath, tempId, attemptId);
                    return Fail(entry, ex.Message);
                }

                // Make the rename durable. A failure here propagates; reconciliation will find the published file.
                temp.Flush();
                _journal.SetPhase(attemptId, AttemptPhase.Published, publishedFileId: tempId);
            }
        }

        return FinishPublished(_journal.GetEntry(entry.Id), _journal.GetOpenAttempt(entry.Id)!);
    }

    private CopyResult FinishPublished(Entry entry, Attempt attempt)
    {
        bool needsVerification = attempt.Phase == AttemptPhase.Published && entry.HashAlgorithm is not null && entry.DestHash is null;
        if (needsVerification)
        {
            string destHash = HashUnbuffered(entry.DestPath, entry.HashAlgorithm!);
            if (destHash != entry.SrcHash)
            {
                _journal.SetPhase(attempt.Id, AttemptPhase.Abandoned);
                return _fs.TryGetIdentity(entry.DestPath) is { } published
                    ? VerificationFailed(entry, replaceTarget: published)
                    : Fail(entry, "The copied file disappeared before it could be verified. The source was kept.");
            }
            _journal.SetHashes(entry.Id, null, destHash);
            _journal.SetPhase(attempt.Id, AttemptPhase.Verified);
        }

        _journal.SetPhase(attempt.Id, AttemptPhase.Finished);
        _journal.SetEntryState(entry.Id, EntryState.Done);
        return new CopyResult(CopyOutcome.Done);
    }

    private string? WriteTemp(ISourceFile source, ITempFile temp, string? algorithm)
    {
        var hasher = algorithm is null ? null : Hashers.Create(algorithm);
        temp.Preallocate(source.Identity.Size);

        var buffer = new byte[_options.ChunkSize];
        long offset = 0;
        while (true)
        {
            int read = source.Read(buffer, offset);
            if (read == 0) break;
            var chunk = buffer.AsSpan(0, read);
            hasher?.Append(chunk);
            temp.Write(chunk, offset);
            offset += read;
        }

        temp.SetLength(offset);
        var s = source.Identity;
        temp.SetTimesAndAttributes(s.CreationTime, s.LastWriteTime, s.Attributes & CopiedAttributes);
        temp.Flush(); // data is on disk before the file can get its final name
        return hasher?.FinishHex();
    }

    private string HashUnbuffered(string path, string algorithm)
    {
        var hasher = Hashers.Create(algorithm);
        using var file = _fs.OpenForVerify(path);
        using var buffer = new AlignedBuffer(_options.ChunkSize);
        long offset = 0;
        while (true)
        {
            int read = file.Read(buffer.Span, offset);
            if (read == 0) break;
            hasher.Append(buffer.Span[..read]);
            offset += read;
            if (read < buffer.Length) break;
        }
        return hasher.FinishHex();
    }

    private ISourceFile OpenSourceWithRetry(string path)
    {
        var deadline = DateTime.UtcNow + _options.SharingRetryTimeout;
        while (true)
        {
            try { return _fs.OpenSource(path); }
            catch (FsException ex) when (ex.IsSharingViolation && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(_options.SharingRetryDelay);
            }
        }
    }

    private void Abandon(ITempFile temp, string tempPath, UInt128 tempId, long attemptId)
    {
        temp.Dispose(); // close our handle so the delete can open the file
        _fs.DeleteIfSameObject(tempPath, tempId);
        _journal.SetPhase(attemptId, AttemptPhase.Abandoned);
    }

    private CopyResult DestinationChanged(Entry entry, ITempFile temp, string tempPath, UInt128 tempId, long attemptId)
    {
        Abandon(temp, tempPath, tempId, attemptId);
        _journal.SetEntryState(entry.Id, EntryState.Pending);
        return new CopyResult(CopyOutcome.DestinationChanged, "The destination changed after the conflict decision.");
    }

    private CopyResult VerificationFailed(Entry entry, FileIdentity? replaceTarget)
    {
        int failures = _journal.RecordVerifyFailure(entry.Id, replaceTarget);
        if (failures >= 2) return Fail(entry, "Verification failed twice. The source was kept.");
        _journal.SetEntryState(entry.Id, EntryState.Pending);
        return new CopyResult(CopyOutcome.RetryNeeded, "Verification failed. The file will be copied again.");
    }

    private CopyResult Fail(Entry entry, string message)
    {
        _journal.SetEntryState(entry.Id, EntryState.Failed, message);
        return new CopyResult(CopyOutcome.Failed, message);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 68 tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: per-file copy protocol with verify-before-replace" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Reconciliation after a crash

**Files:**
- Create: `src/Squeue.Core/Copy/Reconciler.cs`
- Test: `tests/Squeue.Tests/Copy/ReconcilerTests.cs`

**Interfaces:**
- Consumes: `IFileSystem` (Task 1), `Journal` + records (Task 6), `FaultyFileSystem` (Task 7), `FileCopier` + `CopyScenario` (Task 8).
- Produces: `sealed class Reconciler(IFileSystem fs, Journal journal)` with `ReconcileReport Run()`; `sealed class ReconcileReport` with `List<long> Reset` (entries back to pending), `List<long> Resumed` (published, copier will finish them), `List<long> Failed`, `List<string> Unowned` (files at temp names we couldn't prove are ours; left in place).

- [ ] **Step 1: Write the failing tests**

`tests/Squeue.Tests/Copy/ReconcilerTests.cs`:

```csharp
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Copy;

public class ReconcilerTests
{
    private static readonly CopyOptions Options = new() { ChunkSize = 64 * 1024, SharingRetryTimeout = TimeSpan.Zero };
    private readonly WindowsFileSystem _fs = new();

    private long CrashDuringCopy(CopyScenario s, FsOp op, bool after, int occurrence = 1)
    {
        long id = s.AddEntry();
        var faulty = new FaultyFileSystem(_fs) { CrashAt = (op, after, occurrence) };
        Assert.Throws<SimulatedCrashException>(() => new FileCopier(faulty, s.Journal, Options).CopyEntry(id));
        s.ReopenJournal();
        return id;
    }

    [Fact]
    public void Temp_created_but_not_yet_journaled_is_removed()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(1000));
        long id = CrashDuringCopy(s, FsOp.CreateTemp, after: true);
        Assert.Single(s.TempFiles());

        var report = new Reconciler(_fs, s.Journal).Run();

        Assert.Equal(new[] { id }, report.Reset);
        Assert.Empty(s.TempFiles());
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
        Assert.Empty(s.Journal.OpenAttempts());
    }

    [Fact]
    public void Half_written_temp_is_removed_and_the_copy_restarts()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(200_000);
        s.WriteSource(content);
        long id = CrashDuringCopy(s, FsOp.Write, after: true, occurrence: 2);

        var report = new Reconciler(_fs, s.Journal).Run();
        Assert.Equal(new[] { id }, report.Reset);
        Assert.Empty(s.TempFiles());

        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
    }

    [Fact]
    public void Rename_that_happened_before_the_crash_is_recognised_and_resumed()
    {
        using var s = new CopyScenario();
        var content = TestDir.RandomBytes(5000);
        s.WriteSource(content);
        long id = CrashDuringCopy(s, FsOp.Rename, after: true);
        Assert.True(File.Exists(s.Dest));

        var report = new Reconciler(_fs, s.Journal).Run();

        Assert.Equal(new[] { id }, report.Resumed);
        Assert.Equal(AttemptPhase.Published, s.Journal.GetOpenAttempt(id)!.Phase);
        Assert.Equal(CopyOutcome.Done, new FileCopier(_fs, s.Journal, Options).CopyEntry(id).Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Single(s.Journal.AttemptsFor(id)); // not copied twice
    }

    [Fact]
    public void A_file_at_our_temp_name_with_a_different_id_is_left_alone()
    {
        using var s = new CopyScenario();
        s.WriteSource([1]);
        long id = s.AddEntry();
        s.WriteDest([0]);
        string foreign = Path.Combine(s.DestDir, "~tqaaaaaaaaaa.tmp");
        File.WriteAllBytes(foreign, [42]);
        long attempt = s.Journal.BeginAttempt(id, "~tqaaaaaaaaaa.tmp", null);
        s.Journal.SetPhase(attempt, AttemptPhase.TempCreated, tempFileId: (UInt128)12345);

        var report = new Reconciler(_fs, s.Journal).Run();

        Assert.Equal(new[] { foreign }, report.Unowned);
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(foreign));
        Assert.Equal(EntryState.Pending, s.Journal.GetEntry(id).State);
    }

    [Fact]
    public void An_older_file_at_our_temp_name_with_no_journaled_id_is_left_alone()
    {
        using var s = new CopyScenario();
        s.WriteSource([1]);
        long id = s.AddEntry();
        Directory.CreateDirectory(s.DestDir);
        string foreign = Path.Combine(s.DestDir, "~tqbbbbbbbbbb.tmp");
        File.WriteAllBytes(foreign, [42]);
        File.SetCreationTimeUtc(foreign, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        s.Journal.BeginAttempt(id, "~tqbbbbbbbbbb.tmp", null);

        var report = new Reconciler(_fs, s.Journal).Run();

        Assert.Equal(new[] { foreign }, report.Unowned);
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void Published_file_replaced_by_someone_else_fails_the_entry_and_keeps_both()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(5000));
        long id = CrashDuringCopy(s, FsOp.OpenForVerify, after: false); // crash right after publishing
        File.Delete(s.Dest);
        File.WriteAllBytes(s.Dest, [5, 5]);

        var report = new Reconciler(_fs, s.Journal).Run();

        Assert.Equal(new[] { id }, report.Failed);
        Assert.Equal(new byte[] { 5, 5 }, File.ReadAllBytes(s.Dest));
        Assert.True(File.Exists(s.Src));
        Assert.Equal(EntryState.Failed, s.Journal.GetEntry(id).State);
    }

    [Fact]
    public void Running_twice_changes_nothing_the_second_time()
    {
        using var s = new CopyScenario();
        s.WriteSource(TestDir.RandomBytes(1000));
        CrashDuringCopy(s, FsOp.Flush, after: true);
        new Reconciler(_fs, s.Journal).Run();
        var filesAfterFirst = Directory.GetFiles(s.Dir.Path, "*", SearchOption.AllDirectories).Order().ToArray();

        var second = new Reconciler(_fs, s.Journal).Run();

        Assert.Empty(second.Reset);
        Assert.Empty(second.Resumed);
        Assert.Empty(second.Failed);
        Assert.Empty(second.Unowned);
        Assert.Equal(filesAfterFirst, Directory.GetFiles(s.Dir.Path, "*", SearchOption.AllDirectories).Order().ToArray());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ReconcilerTests`
Expected: build FAILS — `Reconciler` doesn't exist.

- [ ] **Step 3: Write the implementation**

`src/Squeue.Core/Copy/Reconciler.cs`:

```csharp
using Squeue.Core.FileSystem;
using Squeue.Core.State;

namespace Squeue.Core.Copy;

public sealed class ReconcileReport
{
    /// Entries whose unfinished attempt was cleaned up; they are pending again.
    public List<long> Reset { get; } = [];

    /// Entries whose file is published; FileCopier.CopyEntry finishes them.
    public List<long> Resumed { get; } = [];

    public List<long> Failed { get; } = [];

    /// Files at a journaled temp name that we couldn't prove are ours. Left untouched.
    public List<string> Unowned { get; } = [];
}

/// After a crash, compares every open journal attempt with what is actually on disk and brings it
/// to a state FileCopier can resume from. Safe to run any number of times.
public sealed class Reconciler(IFileSystem fs, Journal journal)
{
    // Clock slack when deciding whether an un-journaled temp file was created by this attempt (FILETIME units).
    private const long CreationSlack = 2 * 10_000_000;

    public ReconcileReport Run()
    {
        var report = new ReconcileReport();
        foreach (var attempt in journal.OpenAttempts())
            Reconcile(journal.GetEntry(attempt.EntryId), attempt, report);
        return report;
    }

    private void Reconcile(Entry entry, Attempt attempt, ReconcileReport report)
    {
        string tempPath = Path.Combine(entry.DestDirectory, attempt.TempName);

        switch (attempt.Phase)
        {
            case AttemptPhase.Published or AttemptPhase.Verified:
                if (fs.TryGetIdentity(entry.DestPath) is { } published && published.FileId == attempt.PublishedFileId)
                {
                    report.Resumed.Add(entry.Id);
                    return;
                }
                journal.SetPhase(attempt.Id, AttemptPhase.Abandoned);
                journal.SetEntryState(entry.Id, EntryState.Failed, "The destination changed after it was copied. The source was kept.");
                report.Failed.Add(entry.Id);
                return;

            default: // Intent, TempCreated, Written, VerifiedTemp
                // The rename only ever happens after Written is committed, so only those phases can have published.
                bool mayHavePublished = attempt.Phase is AttemptPhase.Written or AttemptPhase.VerifiedTemp;
                if (mayHavePublished
                    && attempt.TempFileId is { } tempId
                    && fs.TryGetIdentity(tempPath) is null
                    && fs.TryGetIdentity(entry.DestPath) is { } final
                    && final.FileId == tempId)
                {
                    journal.SetPhase(attempt.Id, AttemptPhase.Published, publishedFileId: tempId);
                    report.Resumed.Add(entry.Id);
                    return;
                }

                RemoveTempIfOurs(attempt, tempPath, report);
                journal.SetPhase(attempt.Id, AttemptPhase.Abandoned);
                journal.SetEntryState(entry.Id, EntryState.Pending);
                report.Reset.Add(entry.Id);
                return;
        }
    }

    private void RemoveTempIfOurs(Attempt attempt, string tempPath, ReconcileReport report)
    {
        if (fs.TryGetIdentity(tempPath) is not { } found) return;

        bool ours = attempt.TempFileId is { } id
            ? found.FileId == id
            : found.CreationTime >= attempt.StartedAt - CreationSlack; // crashed before the id was journaled

        if (ours) fs.DeleteIfSameObject(tempPath, found.FileId);
        else report.Unowned.Add(tempPath);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 75 tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: crash reconciliation for copy attempts" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Crash matrix

**Files:**
- Test: `tests/Squeue.Tests/Copy/CrashMatrixTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: the invariant tests for I2 (publication is safe), I3 (recovery is idempotent and completes) and I4 (ownership is proven), across every filesystem boundary, for new files and replacements.

- [ ] **Step 1: Write the tests**

`tests/Squeue.Tests/Copy/CrashMatrixTests.cs`:

```csharp
using Squeue.Core.Copy;
using Squeue.Core.FileSystem;
using Squeue.Core.State;
using Squeue.Tests.Fakes;

namespace Squeue.Tests.Copy;

public class CrashMatrixTests
{
    private const int Chunk = 64 * 1024;
    private static readonly CopyOptions Options = new() { ChunkSize = Chunk, SharingRetryTimeout = TimeSpan.Zero };

    public static TheoryData<bool, FsOp, bool, int> CrashPoints()
    {
        var data = new TheoryData<bool, FsOp, bool, int>();
        foreach (bool replace in new[] { false, true })
        foreach (FsOp op in Enum.GetValues<FsOp>())
        foreach (bool after in new[] { false, true })
        {
            data.Add(replace, op, after, 1);
            if (op is FsOp.Write or FsOp.Flush or FsOp.VerifyRead or FsOp.TryGetIdentity) data.Add(replace, op, after, 2);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public void Recovery_after_a_crash_leaves_one_valid_copy_and_touches_nothing_else(bool replace, FsOp op, bool after, int occurrence)
    {
        using var s = new CopyScenario();
        var real = new WindowsFileSystem();
        var content = TestDir.RandomBytes(3 * Chunk + 123, seed: 7);
        var original = TestDir.RandomBytes(5000, seed: 9);
        s.WriteSource(content);

        Directory.CreateDirectory(s.DestDir);
        string decoy = Path.Combine(s.DestDir, "~tqzzzzzzzzzz.tmp"); // looks like ours, isn't
        string unrelated = Path.Combine(s.DestDir, "unrelated.txt");
        File.WriteAllBytes(decoy, [42]);
        File.WriteAllBytes(unrelated, [1, 2, 3]);

        long id;
        if (replace)
        {
            s.WriteDest(original);
            id = s.AddEntry(action: ConflictAction.Replace, seenDest: real.TryGetIdentity(s.Dest));
        }
        else
        {
            id = s.AddEntry();
        }

        var faulty = new FaultyFileSystem(real) { CrashAt = (op, after, occurrence) };
        try { new FileCopier(faulty, s.Journal, Options).CopyEntry(id); }
        catch (SimulatedCrashException) { }

        // I2: at the moment of the crash the final name holds the old file or the complete new one, nothing else.
        AssertOriginalOrNew(s.Dest, replace ? original : null, content);

        // Restart: new database connection, real file system, reconcile, then finish the job.
        s.ReopenJournal();
        new Reconciler(real, s.Journal).Run();
        var copier = new FileCopier(real, s.Journal, Options);
        var result = copier.CopyEntry(id);
        if (result.Outcome == CopyOutcome.RetryNeeded) result = copier.CopyEntry(id);

        Assert.Equal(CopyOutcome.Done, result.Outcome);
        Assert.Equal(content, File.ReadAllBytes(s.Dest));
        Assert.Empty(s.Journal.OpenAttempts());

        // I4: only our own temp files were removed.
        Assert.Equal(new[] { decoy }, s.TempFiles());
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(decoy));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(unrelated));
        Assert.Equal(content, File.ReadAllBytes(s.Src));

        // I3: reconciling again finds nothing to do.
        var again = new Reconciler(real, s.Journal).Run();
        Assert.Empty(again.Reset);
        Assert.Empty(again.Failed);
    }

    private static void AssertOriginalOrNew(string dest, byte[]? original, byte[] content)
    {
        if (!File.Exists(dest))
        {
            Assert.Null(original); // a replacement must never leave the destination missing
            return;
        }
        var actual = File.ReadAllBytes(dest);
        Assert.True(actual.AsSpan().SequenceEqual(content) || (original is not null && actual.AsSpan().SequenceEqual(original)),
            "The destination held a partial or unexpected file after the crash.");
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~CrashMatrixTests`
Expected: PASS — 64 cases. If any case fails, the failure names the crash point (for example `replace: True, op: Rename, after: True, occurrence: 1`); fix the protocol in `FileCopier` or `Reconciler`, never the test's expectations.

- [ ] **Step 3: Run the whole suite**

Run: `dotnet test`
Expected: PASS — 139 tests.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "test: crash matrix across every filesystem boundary" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## What this plan does not cover (and where it goes)

| Spec item | Plan |
| --- | --- |
| Protected source delete, same-volume rename, directories | 2 — Moves |
| Reader/writer threads, batched commits, throughput benchmarks | 3 — Throughput |
| Alternate data streams, extended attributes, EFS, reparse points, cloud placeholders | 4 — File contents |
| Database commit failures and recovery mode | 5 — Coordinator (it owns the writer thread) |
| Endpoints, disks, scheduler, capacity ledger, claims | 5 — Coordinator |
| Conflict rules, planning scans, selection | 6 — Planning |
| Real power-cut and drive-removal tests | Manual test runbook alongside plan 3; process-level fault injection here can't simulate lost cache contents |
