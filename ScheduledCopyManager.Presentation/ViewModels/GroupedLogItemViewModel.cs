using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Enums;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class GroupedLogItemViewModel : ObservableObject
    {
        [ObservableProperty] private bool _isExpanded;

        public Guid Id { get; set; } = Guid.NewGuid();
        public string JobName { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public JobResultStatus Status { get; set; }
        public int TotalFiles { get; set; }
        public int FilesCopied { get; set; }
        public int FilesSkipped { get; set; }
        public int FilesFailed { get; set; }
        public long BytesCopied { get; set; }

        public ObservableCollection<FileItemResultViewModel> ChildFileLogs { get; } = new();

        public string ExpandIcon => IsExpanded ? "▼" : "▶";

        public string SummaryText => $"[{JobName}] — {TotalFiles} dosya ({FilesCopied} Kopyalandı, {FilesFailed} Hatalı, {FilesSkipped} Atlandı) — {(BytesCopied / (1024 * 1024.0)):F1} MB — {Timestamp:dd.MM.yyyy HH:mm:ss}";

        [RelayCommand]
        public void ToggleExpand()
        {
            IsExpanded = !IsExpanded;
            OnPropertyChanged(nameof(ExpandIcon));
        }
    }
}
