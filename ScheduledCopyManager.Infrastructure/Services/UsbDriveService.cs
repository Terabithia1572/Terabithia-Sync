using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Interfaces;

namespace ScheduledCopyManager.Infrastructure.Services
{
    public class UsbDriveService : IUsbDriveService, IDisposable
    {
        public event EventHandler<UsbDriveInfo>? DriveArrived;
        public event EventHandler<string>? DriveRemoved;

        private readonly Timer _timer;
        private readonly HashSet<string> _knownDrives = new(StringComparer.OrdinalIgnoreCase);

        public UsbDriveService()
        {
            // Initial snapshot
            RefreshKnownDrives();

            // Polling timer every 3 seconds for removable drives
            _timer = new Timer(OnPollDrives, null, 3000, 3000);
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

        private void OnPollDrives(object? state)
        {
            try
            {
                var currentRemovables = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Removable && d.IsReady)
                    .Select(d => d.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Check for new drives
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

                // Check for removed drives
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
            long total = 0;
            long free = 0;

            if (d.IsReady)
            {
                try { label = d.VolumeLabel; } catch { }
                try { total = d.TotalSize; } catch { }
                try { free = d.AvailableFreeSpace; } catch { }
            }

            return new UsbDriveInfo
            {
                Name = d.Name,
                VolumeLabel = string.IsNullOrWhiteSpace(label) ? "USB Sürücü" : label,
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
