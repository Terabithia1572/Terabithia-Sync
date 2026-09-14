using System;
using System.IO;
using System.Threading.Tasks;
using ScheduledCopyManager.Infrastructure.Services;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class PathValidationServiceTests : IDisposable
    {
        private readonly string _tempFile;
        private readonly string _tempDir;

        public PathValidationServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "PathVal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _tempFile = Path.Combine(_tempDir, "file.txt");
            File.WriteAllText(_tempFile, "test");
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        // 11. Invalid Source
        [Fact]
        public async Task ValidateSourcesAsync_11_NonExistentSource_ReturnsValidationError()
        {
            var service = new PathValidationService();
            var errors = await service.ValidateSourcesAsync(new[] { Path.Combine(_tempDir, "does_not_exist.txt") });
            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("bulunamadı"));
        }

        // 12. Invalid Destination
        [Fact]
        public async Task ValidateDestinationAsync_12_WhenDestinationIsFile_ReturnsValidationError()
        {
            var service = new PathValidationService();
            var errors = await service.ValidateDestinationAsync(_tempFile);
            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("dosya"));
        }

        // 13. Source Equals Destination
        [Fact]
        public async Task ValidateJobPathsAsync_13_SameSourceAndDestination_ReturnsValidationError()
        {
            var service = new PathValidationService();
            var errors = await service.ValidateJobPathsAsync(new[] { _tempDir }, _tempDir);
            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("aynı"));
        }

        // 14. Recursive Destination
        [Fact]
        public async Task ValidateJobPathsAsync_14_RecursiveDestination_ReturnsValidationError()
        {
            var subDir = Path.Combine(_tempDir, "subfolder");
            Directory.CreateDirectory(subDir);

            var service = new PathValidationService();
            var errors = await service.ValidateJobPathsAsync(new[] { _tempDir }, subDir);
            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("alt klasörü"));
        }
    }
}
