using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class UsbDriveService : IUsbDriveService, IDisposable
    {
        public event EventHandler<UsbDriveInfo>? DriveArrived;
        public event EventHandler<string>? DriveRemoved;

        private readonly Timer _timer;
        private readonly HashSet<string> _knownDrives = new(StringComparer.OrdinalIgnoreCase);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder? volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder? fileSystemNameBuffer,
            int nFileSystemNameSize);

        public UsbDriveService()
        {
            RefreshKnownDrives();
            _timer = new Timer(OnPollDrives, null, 3000, 3000);
        }

        public string? GetVolumeSerialNumber(string driveLetterOrPath)
        {
            if (string.IsNullOrWhiteSpace(driveLetterOrPath)) return null;

            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(driveLetterOrPath)) ?? driveLetterOrPath;
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()) && !root.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                {
                    root += Path.DirectorySeparatorChar;
                }

                uint serialNumber;
                if (GetVolumeInformation(root, null, 0, out serialNumber, out _, out _, null, 0))
                {
                    if (serialNumber == 0) return null;
                    return $"{(serialNumber >> 16):X4}-{(serialNumber & 0xFFFF):X4}";
                }
            }
            catch { }

            return null;
        }

        public string? FindDriveLetterByVolumeSerialNumber(string volumeSerialNumber)
        {
            if (string.IsNullOrWhiteSpace(volumeSerialNumber)) return null;

            try
            {
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady);
                foreach (var drive in drives)
                {
                    string? serial = GetVolumeSerialNumber(drive.Name);
                    if (string.Equals(serial, volumeSerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return drive.Name;
                    }
                }
            }
            catch { }

            return null;
        }

        public IReadOnlyList<UsbDriveInfo> GetRemovableDrives()
        {
            var list = new List<UsbDriveInfo>();
            try
            {
                var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable);
                foreach (var d in drives)
                {
                    list.Add(MapUsbDriveInfo(d));
                }
            }
            catch { }
            return list;
        }

        public bool IsDriveConnected(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return false;

                var drive = new DriveInfo(root);
                return drive.IsReady;
            }
            catch
            {
                return false;
            }
        }

        public bool IsDriveConnected(string path, string? expectedVolumeSerialNumber)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            if (string.IsNullOrWhiteSpace(expectedVolumeSerialNumber))
            {
                return IsDriveConnected(path);
            }

            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return false;

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    // Check if the expected device is connected on a different drive letter
                    string? altDriveLetter = FindDriveLetterByVolumeSerialNumber(expectedVolumeSerialNumber);
                    return !string.IsNullOrEmpty(altDriveLetter);
                }

                string? currentSerial = GetVolumeSerialNumber(root);
                if (string.Equals(currentSerial, expectedVolumeSerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Current drive letter exists but contains a DIFFERENT USB volume (mismatched serial)
                // Never silently write to a different disk! Check if expected volume is connected elsewhere.
                string? relocatedDriveLetter = FindDriveLetterByVolumeSerialNumber(expectedVolumeSerialNumber);
                return !string.IsNullOrEmpty(relocatedDriveLetter);
            }
            catch
            {
                return false;
            }
        }

        private void OnPollDrives(object? state)
        {
            try
            {
                var currentRemovables = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Removable && d.IsReady)
                    .Select(d => d.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var driveName in currentRemovables)
                {
                    if (_knownDrives.Add(driveName))
                    {
                        try
                        {
                            var driveInfo = new DriveInfo(driveName);
                            DriveArrived?.Invoke(this, MapUsbDriveInfo(driveInfo));
                        }
                        catch { }
                    }
                }

                var removed = _knownDrives.Where(d => !currentRemovables.Contains(d)).ToList();
                foreach (var driveName in removed)
                {
                    _knownDrives.Remove(driveName);
                    DriveRemoved?.Invoke(this, driveName);
                }
            }
            catch { }
        }

        private void RefreshKnownDrives()
        {
            try
            {
                var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable && d.IsReady);
                foreach (var d in drives)
                {
                    _knownDrives.Add(d.Name);
                }
            }
            catch { }
        }

        private UsbDriveInfo MapUsbDriveInfo(DriveInfo d)
        {
            string label = string.Empty;
            string serial = string.Empty;
            long total = 0;
            long free = 0;

            if (d.IsReady)
            {
                try { label = d.VolumeLabel; } catch { }
                try { serial = GetVolumeSerialNumber(d.Name) ?? string.Empty; } catch { }
                try { total = d.TotalSize; } catch { }
                try { free = d.AvailableFreeSpace; } catch { }
            }

            return new UsbDriveInfo
            {
                Name = d.Name,
                VolumeLabel = string.IsNullOrWhiteSpace(label) ? "USB Sürücü" : label,
                VolumeSerialNumber = serial,
                TotalSize = total,
                AvailableFreeSpace = free,
                IsReady = d.IsReady
            };
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
