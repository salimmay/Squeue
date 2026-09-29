using System.IO;
using Squeue.ViewModels;

namespace Squeue.App;

internal sealed class SystemDrives : IDriveSource
{
    public IReadOnlyList<DriveInfoLite> ReadyDrives() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d =>
            {
                string letter = d.Name.TrimEnd('\\');
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? letter : $"{d.VolumeLabel} ({letter})";
                return new DriveInfoLite(d.RootDirectory.FullName, label);
            })
            .ToList();
}
