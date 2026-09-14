using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Services;
using ScheduledCopyManager.Presentation.Converters;
using ScheduledCopyManager.Presentation.Services;
using ScheduledCopyManager.Presentation.ViewModels;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class LocalizationAndSelectionTests
    {
        private class DummyPathValidationService : IPathValidationService
        {
            public Task<IReadOnlyList<string>> ValidateSourcesAsync(IEnumerable<string> sourcePaths) => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task<IReadOnlyList<string>> ValidateDestinationAsync(string destinationPath) => Task.FromResult<IReadOnlyList<string>>(new List<string>());
            public Task<IReadOnlyList<string>> ValidateJobPathsAsync(IEnumerable<string> sourcePaths, string destinationPath) => Task.FromResult<IReadOnlyList<string>>(new List<string>());
        }

        private class DummyDialogService : IDialogService
        {
            public List<string> FilesToReturn { get; set; } = new();
            public List<string> FoldersToReturn { get; set; } = new();
            public bool ConfirmationResponse { get; set; } = true;

            public Task<Job?> ShowJobEditorAsync(Job? job = null) => Task.FromResult<Job?>(null);
            public Task ShowHistoryDetailsAsync(HistoryEntry entry) => Task.CompletedTask;
            public Task<bool> ShowConfirmationAsync(string title, string message) => Task.FromResult(ConfirmationResponse);
            public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
            public string? SelectFolder(string title = "Klasör Seçin") => FoldersToReturn.FirstOrDefault();
            public List<string> SelectFolders(string title = "Klasör Seçin") => FoldersToReturn;
            public List<string> SelectFiles(string title = "Dosyaları Seçin") => FilesToReturn;
        }

        [Fact]
        public void EnumToTurkishConverter_TranslatesAllEnumsToTurkish()
        {
            var converter = new EnumToTurkishConverter();

            Assert.Equal("Her Gün", converter.Convert(ScheduleType.Daily, typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Haftalık", converter.Convert(ScheduleType.Weekly, typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Aylık", converter.Convert(ScheduleType.Monthly, typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Tek Seferlik", converter.Convert(ScheduleType.OneTime, typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Gelişmiş Zamanlama", converter.Convert(ScheduleType.Cron, typeof(string), null, CultureInfo.InvariantCulture));

            Assert.Equal("Artımlı Kopyalama", converter.Convert(CopyMode.Incremental, typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Ayna Modu", converter.Convert(CopyMode.Mirror, typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Yalnızca Doğrula", converter.Convert(CopyMode.VerifyOnly, typeof(string), null, CultureInfo.InvariantCulture));

            Assert.Equal("Atla", converter.Convert(ConflictPolicy.Skip, typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Üzerine Yaz", converter.Convert(ConflictPolicy.Overwrite, typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Yeniden Adlandır", converter.Convert(ConflictPolicy.Rename, typeof(string), null, CultureInfo.InvariantCulture));

            Assert.Equal("Kaçırılan görevi hemen çalıştır", converter.Convert(MissedJobBehavior.RunImmediately, typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Kaçırılan görevi atla", converter.Convert(MissedJobBehavior.Skip, typeof(string), null, CultureInfo.InvariantCulture));
            Assert.Equal("Görevi yeniden planla", converter.Convert(MissedJobBehavior.Reschedule, typeof(string), null, CultureInfo.InvariantCulture));

            Assert.Equal("Başarılı", converter.Convert(JobResultStatus.Success, typeof(string), string.Empty, CultureInfo.InvariantCulture));
            Assert.Equal("Başarısız", converter.Convert(JobResultStatus.Failure, typeof(string), string.Empty, CultureInfo.InvariantCulture));
            Assert.Equal("Kısmen Başarılı", converter.Convert(JobResultStatus.PartialSuccess, typeof(string), string.Empty, CultureInfo.InvariantCulture));
        }

        [Fact]
        public void JobEditorViewModel_MultipleFilesAndFolders_PreventsDuplicatesWithNormalization()
        {
            var dialogService = new DummyDialogService
            {
                FilesToReturn = new List<string> { @"C:\Data\file1.txt", @"c:\data\file1.txt\", @"C:\Data\file2.txt" },
                FoldersToReturn = new List<string> { @"C:\SourceFolder", @"c:\sourcefolder\", @"D:\BackupFolder" }
            };

            var vm = new JobEditorViewModel(new DummyPathValidationService(), dialogService);

            vm.AddSourceFile();
            vm.AddSourceFolder();

            // Should contain normalized unique files and folders
            Assert.Equal(4, vm.SourcePaths.Count);
            Assert.Contains(@"C:\Data\file1.txt", vm.SourcePaths);
            Assert.Contains(@"C:\Data\file2.txt", vm.SourcePaths);
            Assert.Contains(@"C:\SourceFolder", vm.SourcePaths);
            Assert.Contains(@"D:\BackupFolder", vm.SourcePaths);
        }

        [Fact]
        public void JobEditorViewModel_CopyModeExplanations_AreTurkishAndInformative()
        {
            var vm = new JobEditorViewModel(new DummyPathValidationService(), new DummyDialogService());

            vm.SelectedCopyMode = CopyMode.Incremental;
            Assert.Contains("Artımlı Kopyalama", vm.CopyModeExplanation);
            Assert.Contains("Hedefte bulunan ve güncel olan dosyalar gereksiz yere tekrar kopyalanmaz", vm.CopyModeExplanation);

            vm.SelectedCopyMode = CopyMode.Mirror;
            Assert.Contains("Ayna Modu", vm.CopyModeExplanation);
            Assert.Contains("Ayna Modu Silme İzni", vm.CopyModeExplanation);

            vm.SelectedCopyMode = CopyMode.VerifyOnly;
            Assert.Contains("Yalnızca Doğrula", vm.CopyModeExplanation);
            Assert.Contains("Gerçek kopyalama işlemi gerçekleştirilmez", vm.CopyModeExplanation);
        }

        [Fact]
        public void JobEditorViewModel_ConflictPolicyExplanations_AreTurkishAndInformative()
        {
            var vm = new JobEditorViewModel(new DummyPathValidationService(), new DummyDialogService());

            vm.SelectedConflictPolicy = ConflictPolicy.Skip;
            Assert.Contains("Atla", vm.ConflictPolicyExplanation);

            vm.SelectedConflictPolicy = ConflictPolicy.Overwrite;
            Assert.Contains("Üzerine Yaz", vm.ConflictPolicyExplanation);
            Assert.Contains("Dikkat: Hedefteki mevcut dosyanın içeriği değiştirilebilir", vm.ConflictPolicyExplanation);

            vm.SelectedConflictPolicy = ConflictPolicy.Rename;
            Assert.Contains("Yeniden Adlandır", vm.ConflictPolicyExplanation);
            Assert.Contains("rapor (1).xlsx", vm.ConflictPolicyExplanation);
        }

        [Fact]
        public async Task JobEditorViewModel_TestJobAsync_SetsOnIzlemeResultMessage()
        {
            var vm = new JobEditorViewModel(new DummyPathValidationService(), new DummyDialogService());
            vm.SourcePaths.Add(@"C:\TestFile.txt");
            vm.DestinationPath = @"D:\Destination";

            await vm.TestJobAsync();

            Assert.False(vm.IsTesting);
            Assert.Null(vm.ValidationErrorMessage);
            Assert.NotNull(vm.TestResultMessage);
            Assert.Contains("Ön İzleme Başarılı", vm.TestResultMessage);
        }

        [Fact]
        public async Task JobEditorViewModel_RemoveSelectedSources_RemovesOnlySelectedItems()
        {
            var dialogService = new DummyDialogService();
            var vm = new JobEditorViewModel(new DummyPathValidationService(), dialogService);

            var item1 = new SourceItemViewModel(@"C:\File1.txt");
            var item2 = new SourceItemViewModel(@"C:\Folder1");
            vm.SourceItems.Add(item1);
            vm.SourceItems.Add(item2);
            vm.SourcePaths.Add(item1.Path);
            vm.SourcePaths.Add(item2.Path);

            await vm.RemoveSelectedSourcesAsync(new List<SourceItemViewModel> { item1 });

            Assert.Single(vm.SourceItems);
            Assert.Single(vm.SourcePaths);
            Assert.Equal(@"C:\Folder1", vm.SourceItems[0].Path);
        }

        [Fact]
        public async Task JobEditorViewModel_ClearAllSources_ClearsAllItems()
        {
            var dialogService = new DummyDialogService { ConfirmationResponse = true };
            var vm = new JobEditorViewModel(new DummyPathValidationService(), dialogService);

            vm.SourceItems.Add(new SourceItemViewModel(@"C:\File1.txt"));
            vm.SourceItems.Add(new SourceItemViewModel(@"C:\Folder1"));
            vm.SourcePaths.Add(@"C:\File1.txt");
            vm.SourcePaths.Add(@"C:\Folder1");

            await vm.ClearAllSourcesAsync();

            Assert.Empty(vm.SourceItems);
            Assert.Empty(vm.SourcePaths);
        }

        [Fact]
        public void UserFriendlyErrorTranslator_TranslatesExceptionsToTurkish()
        {
            var ioEx = new IOException("The process cannot access the file because it is being used by another process.", 32);
            var accessEx = new UnauthorizedAccessException("Access is denied.");

            string translatedIo = UserFriendlyErrorTranslator.Translate(ioEx);
            string translatedAccess = UserFriendlyErrorTranslator.Translate(accessEx);

            Assert.Contains("başka bir program veya kullanıcı tarafından kullanılıyor", translatedIo);
            Assert.Contains("erişim engellendi", translatedAccess);
        }

        [Fact]
        public async Task FileCopyService_RetryFailedFilesAsync_RetriesOnlyFailedItems()
        {
            var service = new FileCopyService();
            var job = new Job { Name = "Retry Job" };

            string tempDir = Path.Combine(Path.GetTempPath(), "RetryTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string src = Path.Combine(tempDir, "source.txt");
                string dest = Path.Combine(tempDir, "dest.txt");
                File.WriteAllText(src, "Retried Content");

                var failedItem = new FileItemResult
                {
                    SourcePath = src,
                    DestinationPath = dest,
                    FileName = "source.txt",
                    FileSize = 15,
                    Status = FileItemStatus.Failed,
                    ErrorMessage = "Geçici hata"
                };

                var retryResult = await service.RetryFailedFilesAsync(job, new List<FileItemResult> { failedItem }, false, null, System.Threading.CancellationToken.None);

                Assert.True(retryResult.Success);
                Assert.Equal(1, retryResult.FilesCopied);
                Assert.Equal(FileItemStatus.Completed, failedItem.Status);
                Assert.True(File.Exists(dest));
                Assert.Equal("Retried Content", File.ReadAllText(dest));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}
