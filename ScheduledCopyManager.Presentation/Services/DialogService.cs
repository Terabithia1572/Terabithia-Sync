using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Presentation.ViewModels;
using ScheduledCopyManager.Presentation.Views;

namespace ScheduledCopyManager.Presentation.Services
{
    public class DialogService : IDialogService
    {
        private readonly IPathValidationService _pathValidationService;
        private readonly IFileCopyService? _fileCopyService;
        private readonly IJobRepository? _jobRepository;
        private readonly IHistoryRepository? _historyRepository;

        public DialogService(
            IPathValidationService pathValidationService,
            IFileCopyService? fileCopyService = null,
            IJobRepository? jobRepository = null,
            IHistoryRepository? historyRepository = null)
        {
            _pathValidationService = pathValidationService ?? throw new ArgumentNullException(nameof(pathValidationService));
            _fileCopyService = fileCopyService;
            _jobRepository = jobRepository;
            _historyRepository = historyRepository;
        }

        public Task<Job?> ShowJobEditorAsync(Job? job = null)
        {
            var vm = new JobEditorViewModel(_pathValidationService, this, _fileCopyService, job);
            var win = new JobEditorView
            {
                DataContext = vm,
                Owner = System.Windows.Application.Current?.MainWindow,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
            };

            vm.RequestClose += (resultJob) =>
            {
                win.DialogResult = resultJob != null;
                win.Close();
            };

            bool? res = win.ShowDialog();
            return Task.FromResult(res == true ? vm.EditingJob : null);
        }

        public Task ShowHistoryDetailsAsync(HistoryEntry entry)
        {
            if (entry == null) return Task.CompletedTask;

            var vm = new HistoryDetailViewModel(entry, _jobRepository!, _fileCopyService!, _historyRepository!, this);
            var win = new HistoryDetailView
            {
                DataContext = vm,
                Owner = System.Windows.Application.Current?.MainWindow,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
            };

            vm.RequestClose += () => win.Close();
            win.ShowDialog();
            return Task.CompletedTask;
        }

        public Task<bool> ShowConfirmationAsync(string title, string message)
        {
            var res = System.Windows.MessageBox.Show(
                message,
                title,
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            return Task.FromResult(res == System.Windows.MessageBoxResult.Yes);
        }

        public Task ShowMessageAsync(string title, string message)
        {
            System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return Task.CompletedTask;
        }

        public string? SelectFolder(string title = "Klasör Seçin")
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = title,
                    Multiselect = false
                };
                if (dialog.ShowDialog() == true)
                {
                    return dialog.FolderName;
                }
            }
            catch
            {
                using var winformsDlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = title,
                    UseDescriptionForTitle = true
                };
                if (winformsDlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    return winformsDlg.SelectedPath;
                }
            }

            return null;
        }

        public List<string> SelectFolders(string title = "Klasör Seçin")
        {
            var list = new List<string>();
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = title,
                    Multiselect = true
                };
                if (dialog.ShowDialog() == true)
                {
                    if (dialog.FolderNames != null && dialog.FolderNames.Length > 0)
                    {
                        list.AddRange(dialog.FolderNames);
                    }
                    else if (!string.IsNullOrEmpty(dialog.FolderName))
                    {
                        list.Add(dialog.FolderName);
                    }
                }
            }
            catch
            {
                var single = SelectFolder(title);
                if (!string.IsNullOrEmpty(single))
                {
                    list.Add(single);
                }
            }
            return list;
        }

        public List<string> SelectFiles(string title = "Dosyaları Seçin")
        {
            var list = new List<string>();
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = title,
                    Multiselect = true,
                    Filter = "Tüm Dosyalar (*.*)|*.*"
                };
                if (dialog.ShowDialog() == true)
                {
                    list.AddRange(dialog.FileNames);
                }
            }
            catch { }
            return list;
        }
    }
}
