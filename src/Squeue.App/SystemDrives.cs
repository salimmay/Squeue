using System.IO;
using Squeue.ViewModels;

namespace Squeue.App;

internal sealed class SystemDrives : IDriveSource
{
    public IReadOnlyList<DriveInfoLite> ReadyDrives()
    {
        var drives = new List<DriveInfoLite>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                string letter = d.Name.TrimEnd('\\');
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? letter : $"{d.VolumeLabel} ({letter})";
                drives.Add(new DriveInfoLite(d.RootDirectory.FullName, label));
            }
            // A drive being removed or locked can fail while we read it; leave it out.
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return drives;
    }
}
