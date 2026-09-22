using System;
using System.Collections.Generic;
using System.IO;

namespace ScheduledCopyManager.Domain.Interfaces
{
    public class UsbDriveInfo
    {
        public string Name { get; set; } = string.Empty; // Drive letter e.g. "E:\"
        public string VolumeLabel { get; set; } = string.Empty;
        public string VolumeSerialNumber { get; set; } = string.Empty; // Formatted hex e.g. "A8F2-3B4E"
        public long TotalSize { get; set; }
        public long AvailableFreeSpace { get; set; }
        public bool IsReady { get; set; }
    }

    public interface IUsbDriveService
    {
        event EventHandler<UsbDriveInfo>? DriveArrived;
        event EventHandler<string>? DriveRemoved;
        IReadOnlyList<UsbDriveInfo> GetRemovableDrives();
        bool IsDriveConnected(string path);
        bool IsDriveConnected(string path, string? expectedVolumeSerialNumber);
        string? GetVolumeSerialNumber(string driveLetterOrPath);
        string? FindDriveLetterByVolumeSerialNumber(string volumeSerialNumber);
    }
}
