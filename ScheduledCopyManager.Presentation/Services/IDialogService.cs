using System.Collections.Generic;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Models;

namespace ScheduledCopyManager.Presentation.Services
{
    public interface IDialogService
    {
        Task<Job?> ShowJobEditorAsync(Job? job = null);
        Task ShowHistoryDetailsAsync(HistoryEntry entry);
        Task ShowLogDetailsAsync(Domain.Interfaces.LogEntry logEntry);
        Task ShowRecoveryDetailsAsync(JobCheckpoint checkpoint);
        Task<bool> ShowConfirmationAsync(string title, string message);
        Task ShowMessageAsync(string title, string message);
        string? SelectFolder(string title = "Klasör Seçin");
        List<string> SelectFolders(string title = "Klasör Seçin");
        List<string> SelectFiles(string title = "Dosyaları Seçin");
    }
}
