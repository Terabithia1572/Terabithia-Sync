using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Infrastructure.Services;
using Xunit;
using Xunit.Abstractions;

namespace ScheduledCopyManager.Tests
{
    public class BufferPerformanceBenchmarkTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _testDir;
        private readonly string _sourceFile;

        public BufferPerformanceBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
            _testDir = Path.Combine(Path.GetTempPath(), "BufferBenchmark_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);

            _sourceFile = Path.Combine(_testDir, "benchmark_100MB.dat");
            // Create a 100 MB dummy test file filled with non-zero bytes
            byte[] chunk = new byte[1024 * 1024]; // 1 MB
            new Random(42).NextBytes(chunk);
            using (var fs = File.Create(_sourceFile))
            {
                for (int i = 0; i < 100; i++)
                {
                    fs.Write(chunk, 0, chunk.Length);
                }
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testDir))
                    Directory.Delete(_testDir, true);
            }
            catch { }
        }

        private async Task<(double ElapsedSec, double AvgMBps, int WriteOps, int SlowWrites, double MaxWriteMs)> RunCopyTest(int bufferSize)
        {
            string destFile = Path.Combine(_testDir, $"dest_{bufferSize}.dat");
            byte[] buffer = new byte[bufferSize];

            int writeOps = 0;
            int slowWrites = 0;
            double maxWriteMs = 0;
            double totalWriteMs = 0;

            var sw = Stopwatch.StartNew();

            using (var sourceStream = new FileStream(_sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize, useAsync: true))
            using (var destStream = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
            {
                int bytesRead;
                while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bufferSize))) > 0)
                {
                    long w0 = Stopwatch.GetTimestamp();
                    await destStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    long w1 = Stopwatch.GetTimestamp();

                    double writeMs = (w1 - w0) * 1000.0 / Stopwatch.Frequency;
                    writeOps++;
                    totalWriteMs += writeMs;
                    if (writeMs > maxWriteMs) maxWriteMs = writeMs;
                    if (writeMs >= 250) slowWrites++;
                }
            }

            sw.Stop();
            double elapsedSec = sw.Elapsed.TotalSeconds;
            double fileSizeMB = new FileInfo(_sourceFile).Length / (1024.0 * 1024.0);
            double avgMBps = fileSizeMB / elapsedSec;

            return (elapsedSec, avgMBps, writeOps, slowWrites, maxWriteMs);
        }

        [Fact]
        public async Task Benchmark_CompareBufferSizes_80KB_256KB_1MB_4MB()
        {
            int[] bufferSizes = new int[]
            {
                80 * 1024,      // 80 KiB
                256 * 1024,     // 256 KiB
                1 * 1024 * 1024,// 1 MiB
                4 * 1024 * 1024 // 4 MiB
            };

            _output.WriteLine("==========================================================================");
            _output.WriteLine("BUFFER SIZE BENCHMARK RESULTS (100 MB File Copy)");
            _output.WriteLine("Buffer Size | Elapsed (s) | Speed (MB/s) | Write Ops | Slow Writes (>=250ms) | Max Write (ms)");
            _output.WriteLine("--------------------------------------------------------------------------");

            foreach (var size in bufferSizes)
            {
                var res = await RunCopyTest(size);
                string label = size >= 1024 * 1024 ? $"{size / (1024 * 1024)} MiB" : $"{size / 1024} KiB";
                _output.WriteLine($"{label,-11} | {res.ElapsedSec,11:F3} | {res.AvgMBps,12:F2} | {res.WriteOps,9} | {res.SlowWrites,21} | {res.MaxWriteMs,14:F2}");
            }
            _output.WriteLine("==========================================================================");
        }
    }
}
