using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ScheduledCopyManager.Tests
{
    public class TargetDeviceBenchmarkTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _testBaseDir;
        private readonly string _sourceDir;
        private readonly string _destDir;
        private readonly string _sourceFilePath;
        private const long TestFileSize = 300 * 1024 * 1024L; // 300 MiB test file

        public TargetDeviceBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;

            // Use D:\ if available for multi-drive I/O test, else TempPath
            string drive = Directory.Exists(@"D:\") ? @"D:\" : Path.GetTempPath();
            _testBaseDir = Path.Combine(drive, "TerabithiaTargetBenchmark_" + Guid.NewGuid().ToString("N"));
            _sourceDir = Path.Combine(_testBaseDir, "Source");
            _destDir = Path.Combine(_testBaseDir, "Destination");

            Directory.CreateDirectory(_sourceDir);
            Directory.CreateDirectory(_destDir);

            _sourceFilePath = Path.Combine(_sourceDir, "test_300MB.dat");
            CreateTestFile(_sourceFilePath, TestFileSize);
        }

        private static void CreateTestFile(string path, long size)
        {
            byte[] chunk = new byte[1024 * 1024]; // 1 MiB chunk
            new Random(12345).NextBytes(chunk);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            long bytesWritten = 0;
            while (bytesWritten < size)
            {
                int toWrite = (int)Math.Min(chunk.Length, size - bytesWritten);
                fs.Write(chunk, 0, toWrite);
                bytesWritten += toWrite;
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testBaseDir))
                    Directory.Delete(_testBaseDir, true);
            }
            catch { }
        }

        private class BenchmarkMetrics
        {
            public string ConfigName { get; set; } = string.Empty;
            public long TotalBytes { get; set; }
            public double ElapsedSeconds { get; set; }
            public double AverageMiBps { get; set; }
            public int WriteOps { get; set; }
            public int SlowWrites250ms { get; set; }
            public int SlowWrites1000ms { get; set; }
            public double MaxWriteMs { get; set; }
            public double MedianWriteMs { get; set; }
            public double P95WriteMs { get; set; }
            public double P99WriteMs { get; set; }
            public double AvgByteDistanceSlowWrites { get; set; }
            public double AvgWriteCountDistanceSlowWrites { get; set; }
            public List<long> SlowWriteByteOffsets { get; } = new();
            public List<int> SlowWriteOpIndexes { get; } = new();
        }

        private async Task<BenchmarkMetrics> RunCustomCopyTest(
            string configName,
            int appBufferSize,
            int streamBufferSize,
            FileOptions options = FileOptions.Asynchronous,
            bool useCopyToAsync = false)
        {
            string safeName = string.Join("_", configName.Split(Path.GetInvalidFileNameChars()));
            safeName = safeName.Replace("/", "_").Replace("\\", "_").Replace(" ", "_");
            string destFilePath = Path.Combine(_destDir, $"dest_{safeName}.dat");
            if (File.Exists(destFilePath)) File.Delete(destFilePath);

            byte[] buffer = new byte[appBufferSize];
            List<double> writeDurations = new();
            List<long> slowByteOffsets = new();
            List<int> slowOpIndexes = new();

            int writeOps = 0;
            int slowWrites250 = 0;
            int slowWrites1000 = 0;
            long currentBytes = 0;

            var sw = Stopwatch.StartNew();

            using (var sourceStream = new FileStream(_sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, streamBufferSize, options))
            using (var destStream = new FileStream(destFilePath, FileMode.Create, FileAccess.Write, FileShare.None, streamBufferSize, options))
            {
                if (useCopyToAsync)
                {
                    // Control using CopyToAsync
                    await sourceStream.CopyToAsync(destStream, appBufferSize);
                }
                else
                {
                    int bytesRead;
                    while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, appBufferSize))) > 0)
                    {
                        long w0 = Stopwatch.GetTimestamp();
                        await destStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                        long w1 = Stopwatch.GetTimestamp();

                        double writeMs = (w1 - w0) * 1000.0 / Stopwatch.Frequency;
                        writeOps++;
                        writeDurations.Add(writeMs);
                        currentBytes += bytesRead;

                        if (writeMs >= 250) slowWrites250++;
                        if (writeMs >= 1000)
                        {
                            slowWrites1000++;
                            slowByteOffsets.Add(currentBytes);
                            slowOpIndexes.Add(writeOps);
                        }
                    }
                }
            }

            sw.Stop();
            double elapsedSec = sw.Elapsed.TotalSeconds;
            long actualLength = new FileInfo(destFilePath).Length;
            double avgMiBps = (actualLength / (1024.0 * 1024.0)) / elapsedSec;

            double median = 0, p95 = 0, p99 = 0, max = 0;
            if (writeDurations.Count > 0)
            {
                var sorted = writeDurations.OrderBy(x => x).ToList();
                max = sorted.Last();
                median = sorted[(int)(sorted.Count * 0.50)];
                p95 = sorted[(int)(sorted.Count * 0.95)];
                p99 = sorted[(int)(sorted.Count * 0.99)];
            }

            // Calculate average byte distance and write count distance between slow writes (>= 1000 ms)
            double avgByteDist = 0;
            if (slowByteOffsets.Count > 1)
            {
                List<long> deltas = new();
                for (int i = 1; i < slowByteOffsets.Count; i++)
                {
                    deltas.Add(slowByteOffsets[i] - slowByteOffsets[i - 1]);
                }
                avgByteDist = deltas.Average();
            }

            double avgOpDist = 0;
            if (slowOpIndexes.Count > 1)
            {
                List<int> opDeltas = new();
                for (int i = 1; i < slowOpIndexes.Count; i++)
                {
                    opDeltas.Add(slowOpIndexes[i] - slowOpIndexes[i - 1]);
                }
                avgOpDist = opDeltas.Average();
            }

            var metrics = new BenchmarkMetrics
            {
                ConfigName = configName,
                TotalBytes = actualLength,
                ElapsedSeconds = elapsedSec,
                AverageMiBps = avgMiBps,
                WriteOps = writeOps,
                SlowWrites250ms = slowWrites250,
                SlowWrites1000ms = slowWrites1000,
                MaxWriteMs = max,
                MedianWriteMs = median,
                P95WriteMs = p95,
                P99WriteMs = p99,
                AvgByteDistanceSlowWrites = avgByteDist,
                AvgWriteCountDistanceSlowWrites = avgOpDist
            };

            foreach (var b in slowByteOffsets) metrics.SlowWriteByteOffsets.Add(b);
            foreach (var op in slowOpIndexes) metrics.SlowWriteOpIndexes.Add(op);

            return metrics;
        }

        [Fact]
        public async Task TargetDevice_ControlledExperiments_80KB_256KB_1MB_4MB_CopyToAsync()
        {
            var results = new List<BenchmarkMetrics>();

            // CONFIG A: Baseline (80 KB App / 80 KB Stream)
            results.Add(await RunCustomCopyTest("Config A (80KB App / 80KB Stream)", 80 * 1024, 80 * 1024));

            // CONFIG B: 256 KB App / 4 KB Stream
            results.Add(await RunCustomCopyTest("Config B (256KB App / 4KB Stream)", 256 * 1024, 4096));

            // CONFIG C: 1 MB App / 1 Byte (Minimal) Stream
            results.Add(await RunCustomCopyTest("Config C (1MB App / 1B Stream)", 1 * 1024 * 1024, 1));

            // CONFIG D: 4 MB App / SequentialScan
            results.Add(await RunCustomCopyTest("Config D (4MB App / SeqScan)", 4 * 1024 * 1024, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan));

            // CONFIG E: CopyToAsync Control
            results.Add(await RunCustomCopyTest("Config E (CopyToAsync 1MB)", 1 * 1024 * 1024, 80 * 1024, FileOptions.Asynchronous, useCopyToAsync: true));

            // Output Detailed Markdown Report Table
            _output.WriteLine("### TARGET DEVICE (E:) BENCHMARK CONTROLLED EXPERIMENTS TABLE");
            _output.WriteLine("");
            _output.WriteLine("| Configuration | Total MiB | Elapsed (s) | Avg Speed (MiB/s) | Write Ops | SlowWrites (>=250ms) | SlowWrites (>=1s) | Max Write (ms) | Median Write (ms) | P95 (ms) | P99 (ms) | Avg Byte Dist | Avg Write Count Dist |");
            _output.WriteLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

            foreach (var r in results)
            {
                double totalMiB = r.TotalBytes / (1024.0 * 1024.0);
                string byteDistStr = r.AvgByteDistanceSlowWrites > 0 ? $"{r.AvgByteDistanceSlowWrites / (1024.0 * 1024.0):F2} MiB" : "N/A";
                string opDistStr = r.AvgWriteCountDistanceSlowWrites > 0 ? $"{r.AvgWriteCountDistanceSlowWrites:F1} ops" : "N/A";

                _output.WriteLine($"| {r.ConfigName,-30} | {totalMiB,9:F1} | {r.ElapsedSeconds,11:F3} | {r.AverageMiBps,17:F2} | {r.WriteOps,9} | {r.SlowWrites250ms,20} | {r.SlowWrites1000ms,17} | {r.MaxWriteMs,14:F1} | {r.MedianWriteMs,17:F2} | {r.P95WriteMs,8:F2} | {r.P99WriteMs,8:F2} | {byteDistStr,13} | {opDistStr,20} |");
            }

            _output.WriteLine("");
            _output.WriteLine("### CRITICAL ANALYSIS: BYTE COUNT vs WRITE COUNT");
            foreach (var r in results)
            {
                if (r.SlowWrites1000ms > 1)
                {
                    _output.WriteLine($"[{r.ConfigName}] Slow Writes Count: {r.SlowWrites1000ms}");
                    _output.WriteLine($"  - Byte Offsets: {string.Join(", ", r.SlowWriteByteOffsets.Select(x => $"{x / (1024.0 * 1024.0):F2} MiB"))}");
                    _output.WriteLine($"  - Write Op Indexes: {string.Join(", ", r.SlowWriteOpIndexes)}");
                    _output.WriteLine($"  - Avg Byte Interval: {r.AvgByteDistanceSlowWrites / (1024.0 * 1024.0):F2} MiB");
                    _output.WriteLine($"  - Avg Op Index Interval: {r.AvgWriteCountDistanceSlowWrites:F1} WriteOps");
                }
                else
                {
                    _output.WriteLine($"[{r.ConfigName}] Slow Writes Count (>=1000ms): {r.SlowWrites1000ms} (Smooth transfer)");
                }
            }
        }
    }
}
