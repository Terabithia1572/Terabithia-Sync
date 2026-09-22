using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ScheduledCopyManager.Tests
{
    public class ProductionBufferABBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public ProductionBufferABBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static string GetSourceFilePath()
        {
            string candidate1 = @"C:\Users\Administrator\Desktop\JCATS\2.iso";
            if (File.Exists(candidate1)) return candidate1;

            string candidate2 = @"C:\Users\Yunus\Documents\Euro Truck Simulator 2\mod\frosty_v10_3.scs";
            if (File.Exists(candidate2)) return candidate2;

            var files = new[]
            {
                @"C:\Users\Yunus\.android\avd\Medium_Phone.avd\snapshots\default_boot\ram.img",
                @"C:\Users\Yunus\Videos\NVIDIA\Zula\Zula 2026.06.25 - 20.32.08.01.mp4"
            };

            foreach (var f in files)
            {
                if (File.Exists(f)) return f;
            }

            throw new FileNotFoundException("Could not find a large (>1 GB) source test file for benchmark.");
        }

        private static string GetDestinationDirectory()
        {
            if (Directory.Exists(@"E:\"))
            {
                string dir = @"E:\BenchmarkDest";
                Directory.CreateDirectory(dir);
                return dir;
            }
            if (Directory.Exists(@"D:\"))
            {
                string dir = @"D:\BenchmarkDest";
                Directory.CreateDirectory(dir);
                return dir;
            }
            string tempDir = Path.Combine(Path.GetTempPath(), "BenchmarkDest");
            Directory.CreateDirectory(tempDir);
            return tempDir;
        }

        public class BenchmarkMetrics
        {
            public string Name { get; set; } = string.Empty;
            public int ApplicationBufferSize { get; set; }
            public long TotalBytes { get; set; }
            public double TotalElapsedSeconds { get; set; }
            public double AverageMiBPerSecond { get; set; }
            public int ReadOperationCount { get; set; }
            public int WriteOperationCount { get; set; }
            public double AverageReadMs { get; set; }
            public double MedianReadMs { get; set; }
            public double P95ReadMs { get; set; }
            public double P99ReadMs { get; set; }
            public double MaxReadMs { get; set; }
            public double AverageWriteMs { get; set; }
            public double MedianWriteMs { get; set; }
            public double P95WriteMs { get; set; }
            public double P99WriteMs { get; set; }
            public double MaxWriteMs { get; set; }
            public int WriteCount250ms { get; set; }
            public int WriteCount500ms { get; set; }
            public int WriteCount1000ms { get; set; }
            public double LongestNoProgressMs { get; set; }
        }

        private static double GetPercentile(List<double> sequence, double percentile)
        {
            if (sequence.Count == 0) return 0;
            var sorted = sequence.OrderBy(x => x).ToList();
            int idx = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            idx = Math.Clamp(idx, 0, sorted.Count - 1);
            return sorted[idx];
        }

        private async Task<BenchmarkMetrics> RunSingleBufferBenchmarkAsync(string name, string sourcePath, string destDir, int appBufferSize)
        {
            string destPath = Path.Combine(destDir, $"bench_{appBufferSize}_{Guid.NewGuid():N}.dat");
            try
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(appBufferSize);
                List<double> readMsList = new();
                List<double> writeMsList = new();
                List<double> writeCompletionTimesMs = new();

                var sourceOptions = new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    BufferSize = 4096,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                };

                var destOptions = new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                };

                long totalBytes = 0;
                long overallStart = Stopwatch.GetTimestamp();

                try
                {
                    using var sourceStream = new FileStream(sourcePath, sourceOptions);
                    using var destStream = new FileStream(destPath, destOptions);

                    while (true)
                    {
                        long r0 = Stopwatch.GetTimestamp();
                        int bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, appBufferSize));
                        long r1 = Stopwatch.GetTimestamp();
                        double readMs = (r1 - r0) * 1000.0 / Stopwatch.Frequency;
                        readMsList.Add(readMs);

                        if (bytesRead <= 0) break;

                        long w0 = Stopwatch.GetTimestamp();
                        await destStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                        long w1 = Stopwatch.GetTimestamp();
                        double writeMs = (w1 - w0) * 1000.0 / Stopwatch.Frequency;
                        writeMsList.Add(writeMs);

                        double writeCompMs = (w1 - overallStart) * 1000.0 / Stopwatch.Frequency;
                        writeCompletionTimesMs.Add(writeCompMs);

                        totalBytes += bytesRead;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                long overallEnd = Stopwatch.GetTimestamp();
                double totalSec = (overallEnd - overallStart) / (double)Stopwatch.Frequency;

                // Longest period with no completed WriteAsync
                double longestNoProgressMs = 0;
                double prevComp = 0;
                foreach (var comp in writeCompletionTimesMs)
                {
                    double gap = comp - prevComp;
                    if (gap > longestNoProgressMs) longestNoProgressMs = gap;
                    prevComp = comp;
                }

                return new BenchmarkMetrics
                {
                    Name = name,
                    ApplicationBufferSize = appBufferSize,
                    TotalBytes = totalBytes,
                    TotalElapsedSeconds = totalSec,
                    AverageMiBPerSecond = (totalBytes / 1048576.0) / totalSec,
                    ReadOperationCount = readMsList.Count,
                    WriteOperationCount = writeMsList.Count,
                    AverageReadMs = readMsList.Count > 0 ? readMsList.Average() : 0,
                    MedianReadMs = GetPercentile(readMsList, 0.50),
                    P95ReadMs = GetPercentile(readMsList, 0.95),
                    P99ReadMs = GetPercentile(readMsList, 0.99),
                    MaxReadMs = readMsList.Count > 0 ? readMsList.Max() : 0,
                    AverageWriteMs = writeMsList.Count > 0 ? writeMsList.Average() : 0,
                    MedianWriteMs = GetPercentile(writeMsList, 0.50),
                    P95WriteMs = GetPercentile(writeMsList, 0.95),
                    P99WriteMs = GetPercentile(writeMsList, 0.99),
                    MaxWriteMs = writeMsList.Count > 0 ? writeMsList.Max() : 0,
                    WriteCount250ms = writeMsList.Count(w => w >= 250),
                    WriteCount500ms = writeMsList.Count(w => w >= 500),
                    WriteCount1000ms = writeMsList.Count(w => w >= 1000),
                    LongestNoProgressMs = longestNoProgressMs
                };
            }
            finally
            {
                if (File.Exists(destPath))
                {
                    try { File.Delete(destPath); } catch { }
                }
            }
        }

        private async Task<BenchmarkMetrics> RunTwoBufferPipelineBenchmarkAsync(string name, string sourcePath, string destDir, int appBufferSize)
        {
            string destPath = Path.Combine(destDir, $"bench_pipe_{appBufferSize}_{Guid.NewGuid():N}.dat");
            try
            {
                byte[] bufA = ArrayPool<byte>.Shared.Rent(appBufferSize);
                byte[] bufB = ArrayPool<byte>.Shared.Rent(appBufferSize);

                List<double> readMsList = new();
                List<double> writeMsList = new();
                List<double> writeCompletionTimesMs = new();

                var sourceOptions = new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    BufferSize = 4096,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                };

                var destOptions = new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                };

                long totalBytes = 0;
                long overallStart = Stopwatch.GetTimestamp();

                try
                {
                    using var sourceStream = new FileStream(sourcePath, sourceOptions);
                    using var destStream = new FileStream(destPath, destOptions);

                    // Double-buffering ping-pong setup
                    byte[] currentWriteBuf = bufA;
                    byte[] currentReadBuf = bufB;

                    // Initial read
                    long r0 = Stopwatch.GetTimestamp();
                    int bytesRead = await sourceStream.ReadAsync(currentWriteBuf.AsMemory(0, appBufferSize));
                    long r1 = Stopwatch.GetTimestamp();
                    readMsList.Add((r1 - r0) * 1000.0 / Stopwatch.Frequency);

                    while (bytesRead > 0)
                    {
                        int bytesToWrite = bytesRead;

                        // Start next read concurrently with write
                        Task<int> nextReadTask = sourceStream.ReadAsync(currentReadBuf.AsMemory(0, appBufferSize)).AsTask();

                        long w0 = Stopwatch.GetTimestamp();
                        await destStream.WriteAsync(currentWriteBuf.AsMemory(0, bytesToWrite));
                        long w1 = Stopwatch.GetTimestamp();
                        double writeMs = (w1 - w0) * 1000.0 / Stopwatch.Frequency;
                        writeMsList.Add(writeMs);

                        double writeCompMs = (w1 - overallStart) * 1000.0 / Stopwatch.Frequency;
                        writeCompletionTimesMs.Add(writeCompMs);

                        totalBytes += bytesToWrite;

                        // Await next read
                        long rStart = Stopwatch.GetTimestamp();
                        bytesRead = await nextReadTask;
                        long rEnd = Stopwatch.GetTimestamp();
                        readMsList.Add((rEnd - rStart) * 1000.0 / Stopwatch.Frequency);

                        // Swap buffers
                        var temp = currentWriteBuf;
                        currentWriteBuf = currentReadBuf;
                        currentReadBuf = temp;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(bufA);
                    ArrayPool<byte>.Shared.Return(bufB);
                }

                long overallEnd = Stopwatch.GetTimestamp();
                double totalSec = (overallEnd - overallStart) / (double)Stopwatch.Frequency;

                double longestNoProgressMs = 0;
                double prevComp = 0;
                foreach (var comp in writeCompletionTimesMs)
                {
                    double gap = comp - prevComp;
                    if (gap > longestNoProgressMs) longestNoProgressMs = gap;
                    prevComp = comp;
                }

                return new BenchmarkMetrics
                {
                    Name = name,
                    ApplicationBufferSize = appBufferSize,
                    TotalBytes = totalBytes,
                    TotalElapsedSeconds = totalSec,
                    AverageMiBPerSecond = (totalBytes / 1048576.0) / totalSec,
                    ReadOperationCount = readMsList.Count,
                    WriteOperationCount = writeMsList.Count,
                    AverageReadMs = readMsList.Count > 0 ? readMsList.Average() : 0,
                    MedianReadMs = GetPercentile(readMsList, 0.50),
                    P95ReadMs = GetPercentile(readMsList, 0.95),
                    P99ReadMs = GetPercentile(readMsList, 0.99),
                    MaxReadMs = readMsList.Count > 0 ? readMsList.Max() : 0,
                    AverageWriteMs = writeMsList.Count > 0 ? writeMsList.Average() : 0,
                    MedianWriteMs = GetPercentile(writeMsList, 0.50),
                    P95WriteMs = GetPercentile(writeMsList, 0.95),
                    P99WriteMs = GetPercentile(writeMsList, 0.99),
                    MaxWriteMs = writeMsList.Count > 0 ? writeMsList.Max() : 0,
                    WriteCount250ms = writeMsList.Count(w => w >= 250),
                    WriteCount500ms = writeMsList.Count(w => w >= 500),
                    WriteCount1000ms = writeMsList.Count(w => w >= 1000),
                    LongestNoProgressMs = longestNoProgressMs
                };
            }
            finally
            {
                if (File.Exists(destPath))
                {
                    try { File.Delete(destPath); } catch { }
                }
            }
        }

        private static (double miBps, double seconds) RunRobocopyBenchmark(string sourcePath, string destDir)
        {
            string sourceDir = Path.GetDirectoryName(sourcePath)!;
            string fileName = Path.GetFileName(sourcePath);

            var psi = new ProcessStartInfo
            {
                FileName = "robocopy.exe",
                Arguments = $"\"{sourceDir}\" \"{destDir}\" \"{fileName}\" /NJH /NJS /NC /NS /NP",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            long fileSize = new FileInfo(sourcePath).Length;
            var sw = Stopwatch.StartNew();
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            sw.Stop();

            double sec = sw.Elapsed.TotalSeconds;
            double mib = fileSize / (1024.0 * 1024.0);
            double mibps = sec > 0 ? mib / sec : 0;

            string copiedFile = Path.Combine(destDir, fileName);
            if (File.Exists(copiedFile))
            {
                try { File.Delete(copiedFile); } catch { }
            }

            return (mibps, sec);
        }

        [Fact]
        public async Task ExecuteFullProductionBufferABBenchmark()
        {
            string sourcePath = GetSourceFilePath();
            string destDir = GetDestinationDirectory();
            long fileSize = new FileInfo(sourcePath).Length;

            _output.WriteLine($"BENCHMARK SOURCE: {sourcePath} ({fileSize / (1024.0 * 1024.0):N2} MiB)");
            _output.WriteLine($"BENCHMARK DESTINATION: {destDir}");
            _output.WriteLine(new string('=', 90));

            int[] bufferSizes = new[]
            {
                256 * 1024,
                512 * 1024,
                1 * 1024 * 1024,
                2 * 1024 * 1024,
                4 * 1024 * 1024
            };

            List<BenchmarkMetrics> results = new();

            foreach (int size in bufferSizes)
            {
                string name = size >= 1024 * 1024 ? $"{size / (1024 * 1024)} MiB" : $"{size / 1024} KiB";
                _output.WriteLine($"Running benchmark for {name}...");
                var metrics = await RunSingleBufferBenchmarkAsync(name, sourcePath, destDir, size);
                results.Add(metrics);
                _output.WriteLine($"Completed {name}: {metrics.AverageMiBPerSecond:N2} MiB/s, Max Write = {metrics.MaxWriteMs:N1} ms, >=1000ms Writes = {metrics.WriteCount1000ms}");
            }

            // Two-buffer pipeline test on 512 KiB and 1 MiB
            _output.WriteLine("Running 2-Buffer Pipeline benchmark (512 KiB)...");
            var pipe512 = await RunTwoBufferPipelineBenchmarkAsync("512 KiB (2-Buffer Pipeline)", sourcePath, destDir, 512 * 1024);
            results.Add(pipe512);

            _output.WriteLine("Running 2-Buffer Pipeline benchmark (1 MiB)...");
            var pipe1M = await RunTwoBufferPipelineBenchmarkAsync("1 MiB (2-Buffer Pipeline)", sourcePath, destDir, 1024 * 1024);
            results.Add(pipe1M);

            // Robocopy benchmark
            _output.WriteLine("Running Robocopy benchmark...");
            var (roboMiBps, roboSec) = RunRobocopyBenchmark(sourcePath, destDir);
            _output.WriteLine($"Robocopy: {roboMiBps:N2} MiB/s ({roboSec:N2} sec)");
            _output.WriteLine(new string('=', 90));

            // Print full summary table
            _output.WriteLine("\nFULL BENCHMARK RESULTS TABLE:");
            _output.WriteLine(string.Format("{0,-25} | {1,8} | {2,8} | {3,8} | {4,8} | {5,8} | {6,8} | {7,8} | {8,8} | {9,8}",
                "Config", "Throughput", "Max Write", "Avg Write", "Med Write", "P95 Write", ">=250ms", ">=500ms", ">=1000ms", "Max NoProg"));
            _output.WriteLine(new string('-', 120));

            foreach (var r in results)
            {
                _output.WriteLine(string.Format("{0,-25} | {1,6:N2} MB/s | {2,6:N1} ms | {3,6:N1} ms | {4,6:N1} ms | {5,6:N1} ms | {6,8} | {7,8} | {8,8} | {9,6:N1} ms",
                    r.Name,
                    r.AverageMiBPerSecond,
                    r.MaxWriteMs,
                    r.AverageWriteMs,
                    r.MedianWriteMs,
                    r.P95WriteMs,
                    r.WriteCount250ms,
                    r.WriteCount500ms,
                    r.WriteCount1000ms,
                    r.LongestNoProgressMs));
            }

            _output.WriteLine($"\nROBOCOPY THROUGHPUT: {roboMiBps:N2} MiB/s");
        }
    }
}
