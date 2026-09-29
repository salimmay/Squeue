using Squeue.Core.FileSystem;

namespace Squeue.Tests.Fakes;

public sealed class FakeVolumeSpace(long free, long total = 1L << 40, long cluster = 4096) : IVolumeSpace
{
    public VolumeSpace? Query(string path) => new VolumeSpace(Path.GetPathRoot(Path.GetFullPath(path))!, free, total, cluster);
}
