using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Presentation.ViewModels
{
    public partial class LogDetailViewModel : ObservableObject
    {
        public event Action? RequestClose;

        public LogEntry Entry { get; }

        public string FormattedTimestamp => Entry.Timestamp.ToString("dd.MM.yyyy HH:mm:ss.fff");
        public string Level => Entry.Level;
        public string Message => Entry.Message;
        public string Exception => Entry.Exception ?? string.Empty;
        public bool HasException => !string.IsNullOrWhiteSpace(Entry.Exception);

        public LogDetailViewModel(LogEntry entry)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        }

        [RelayCommand]
        public void CopyToClipboard()
        {
            try
            {
                string text = BuildDiagnosticSummary();
                System.Windows.Clipboard.SetText(text);
            }
            catch { }
        }

        public string BuildDiagnosticSummary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== TERABITHIA SYNC GÜNLÜK DETAYI ===");
            sb.AppendLine($"Zaman: {FormattedTimestamp}");
            sb.AppendLine($"Seviye: {Level}");
            sb.AppendLine($"BuildId: {BuildInfo.BuildId}");
            sb.AppendLine("-------------------------------------");
            sb.AppendLine($"Mesaj:\n{Message}");
            if (HasException)
            {
                sb.AppendLine("-------------------------------------");
                sb.AppendLine($"Hata Detayı / Stack Trace:\n{Exception}");
            }
            sb.AppendLine("=====================================");
            return sb.ToString();
        }

        [RelayCommand]
        public void Close()
        {
            RequestClose?.Invoke();
        }
    }
}
