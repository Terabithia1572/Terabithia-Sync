using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public class RemovableDriveItemViewModel
    {
        public string DriveLetter { get; set; } = string.Empty;
        public string VolumeLabel { get; set; } = string.Empty;
        public string VolumeSerialNumber { get; set; } = string.Empty;
        public string FileSystem { get; set; } = string.Empty;
        public long TotalSize { get; set; }
        public long AvailableFreeSpace { get; set; }
        public string Status { get; set; } = "Hazır";

        public string DisplayLabel => string.IsNullOrWhiteSpace(VolumeLabel) ? "Adsız Sürücü" : VolumeLabel;
        public string FormattedTotalSize => Helpers.FormattingHelpers.FormatBytes(TotalSize);
        public string FormattedFreeSpace => Helpers.FormattingHelpers.FormatBytes(AvailableFreeSpace);
    }

    public partial class ToolsViewModel : ObservableObject
    {
        private readonly IUsbDriveService? _usbDriveService;
        private readonly ILogService _logService;

        public ObservableCollection<RemovableDriveItemViewModel> RemovableDrives { get; } = new();

        public bool HasRemovableDrives => RemovableDrives.Count > 0;

        // Diagnostics read-only properties
        public string AppVersion => BuildInfo.VersionNumber;
        public string BuildId => BuildInfo.BuildId;
        public string Runtime => $".NET 8.0 ({RuntimeInformation.FrameworkDescription})";
        public string OperatingSystem => Environment.OSVersion.ToString();
        public string ProcessArchitecture => RuntimeInformation.ProcessArchitecture.ToString();
        public string AppDataPath { get; private set; } = string.Empty;
        public string LogPath { get; private set; } = string.Empty;
        public string CheckpointPath { get; private set; } = string.Empty;

        public ToolsViewModel(ILogService logService, IUsbDriveService? usbDriveService = null)
        {
            _logService = logService;
            _usbDriveService = usbDriveService;

            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Terabithia Sync");
            AppDataPath = appData;
            LogPath = _logService.GetLogDirectory();
            CheckpointPath = Path.Combine(appData, "checkpoints");
        }

        public void Initialize()
        {
            RefreshDrives();
        }

        [RelayCommand]
        public void RefreshDrives()
        {
            RemovableDrives.Clear();
            if (_usbDriveService != null)
            {
                var drives = _usbDriveService.GetRemovableDrives();
                foreach (var d in drives)
                {
                    string fs = string.Empty;
                    try
                    {
                        var info = new DriveInfo(d.Name);
                        if (info.IsReady) fs = info.DriveFormat;
                    }
                    catch { }

                    RemovableDrives.Add(new RemovableDriveItemViewModel
                    {
                        DriveLetter = d.Name,
                        VolumeLabel = d.VolumeLabel,
                        VolumeSerialNumber = d.VolumeSerialNumber,
                        FileSystem = fs,
                        TotalSize = d.TotalSize,
                        AvailableFreeSpace = d.AvailableFreeSpace,
                        Status = d.IsReady ? "Bağlı / Hazır" : "Erişilemiyor"
                    });
                }
            }

            OnPropertyChanged(nameof(HasRemovableDrives));
        }

        [RelayCommand]
        public void OpenDataFolder()
        {
            OpenFolder(AppDataPath);
        }

        [RelayCommand]
        public void OpenLogFolder()
        {
            OpenFolder(LogPath);
        }

        [RelayCommand]
        public void OpenCheckpointFolder()
        {
            OpenFolder(CheckpointPath);
        }

        private static void OpenFolder(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
                Process.Start("explorer.exe", path);
            }
            catch { }
        }

        [RelayCommand]
        public void CopyDiagnosticsToClipboard()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== TERABITHIA SYNC TANILAMA BİLGİLERİ ===");
                sb.AppendLine($"Uygulama Sürümü: {AppVersion}");
                sb.AppendLine($"BuildId: {BuildId}");
                sb.AppendLine($"Çalışma Zamanı (Runtime): {Runtime}");
                sb.AppendLine($"İşletim Sistemi: {OperatingSystem}");
                sb.AppendLine($"Mimari: {ProcessArchitecture}");
                sb.AppendLine($"Zaman: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
                sb.AppendLine("------------------------------------------");
                sb.AppendLine($"Veri Klasörü: {AppDataPath}");
                sb.AppendLine($"Log Klasörü: {LogPath}");
                sb.AppendLine($"Checkpoint Klasörü: {CheckpointPath}");
                sb.AppendLine("------------------------------------------");
                sb.AppendLine($"Bağlı USB Sürücü Sayısı: {RemovableDrives.Count}");
                foreach (var d in RemovableDrives)
                {
                    sb.AppendLine($"  - {d.DriveLetter} [{d.DisplayLabel}] Serial: {d.VolumeSerialNumber} ({d.FormattedFreeSpace} boş / {d.FormattedTotalSize})");
                }
                sb.AppendLine("==========================================");

                System.Windows.Clipboard.SetText(sb.ToString());
            }
            catch { }
        }
    }
}
