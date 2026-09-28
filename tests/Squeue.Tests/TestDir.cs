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
