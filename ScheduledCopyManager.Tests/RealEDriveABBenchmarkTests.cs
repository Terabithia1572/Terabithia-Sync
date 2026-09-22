using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ScheduledCopyManager.Tests
{
    public class RealEDriveABBenchmarkTests
    {
        private readonly ITestOutputHelper _output;

        public RealEDriveABBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static string GetSourceFilePath()
        {
            string candidate = @"C:\Users\Yunus\Documents\Euro Truck Simulator 2\mod\frosty_v10_3.scs";
            if (File.Exists(candidate)) return candidate;

            string candidate2 = @"C:\Users\Administrator\Desktop\JCATS\2.iso";
            if (File.Exists(candidate2)) return candidate2;

            throw new FileNotFoundException("Source file not found for E: drive benchmark.");
        }

        private static string GetEDriveDestinationDir()
        {
            string dir = @"E:\BenchmarkDest";
            Directory.CreateDirectory(dir);
            return dir;
        }

        public class ExtendedBenchmarkMetrics
        {
            public string ConfigName { get; set; } = string.Empty;
            public int AppBufferSize { get; set; }
            public long TotalBytes { get; set; }
            public double ElapsedSeconds { get; set; }
            public double AverageMiBps { get; set; }
            public int ReadOps { get; set; }
            public int WriteOps { get; set; }
            public double AvgReadMs { get; set; }
            public double P95ReadMs { get; set; }
            public double P99ReadMs { get; set; }
            public double MaxReadMs { get; set; }
            public double AvgWriteMs { get; set; }
            public double MedianWriteMs { get; set; }
            public double P95WriteMs { get; set; }
            public double P99WriteMs { get; set; }
            public double MaxWriteMs { get; set; }
            public int WriteCount250ms { get; set; }
            public int WriteCount500ms { get; set; }
            public int WriteCount1000ms { get; set; }
            public double MaxNoCompletedWriteMs { get; set; }
            public List<(long byteOffset, double writeMs, double gapMs)> SlowWriteEvents { get; } = new();
        }

        private static double GetPercentile(List<double> sequence, double percentile)
        {
            if (sequence.Count == 0) return 0;
            var sorted = sequence.OrderBy(x => x).ToList();
            int idx = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            idx = Math.Clamp(idx, 0, sorted.Count - 1);
            return sorted[idx];
        }

        private async Task<ExtendedBenchmarkMetrics> RunSingleBufferTest(string configName, string sourcePath, string destDir, int appBufferSize)
        {
            string destPath = Path.Combine(destDir, $"bench_e_single_{appBufferSize}_{Guid.NewGuid():N}.dat");
            try
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(appBufferSize);
                List<double> readMsList = new();
                List<double> writeMsList = new();
                List<double> writeCompTimesMs = new();
                List<(long byteOffset, double writeMs, double gapMs)> slowWrites = new();

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
                double lastCompMs = 0;

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

                        double compMs = (w1 - overallStart) * 1000.0 / Stopwatch.Frequency;
                        writeCompTimesMs.Add(compMs);
                        double gap = compMs - lastCompMs;
                        lastCompMs = compMs;

                        if (writeMs >= 250)
                        {
                            slowWrites.Add((totalBytes, writeMs, gap));
                        }

                        totalBytes += bytesRead;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                long overallEnd = Stopwatch.GetTimestamp();
                double totalSec = (overallEnd - overallStart) / (double)Stopwatch.Frequency;

                double maxGap = 0;
                double prevComp = 0;
                foreach (var c in writeCompTimesMs)
                {
                    double gap = c - prevComp;
                    if (gap > maxGap) maxGap = gap;
                    prevComp = c;
                }

                var metrics = new ExtendedBenchmarkMetrics
                {
                    ConfigName = configName,
                    AppBufferSize = appBufferSize,
                    TotalBytes = totalBytes,
                    ElapsedSeconds = totalSec,
                    AverageMiBps = (totalBytes / 1048576.0) / totalSec,
                    ReadOps = readMsList.Count,
                    WriteOps = writeMsList.Count,
                    AvgReadMs = readMsList.Count > 0 ? readMsList.Average() : 0,
                    P95ReadMs = GetPercentile(readMsList, 0.95),
                    P99ReadMs = GetPercentile(readMsList, 0.99),
                    MaxReadMs = readMsList.Count > 0 ? readMsList.Max() : 0,
                    AvgWriteMs = writeMsList.Count > 0 ? writeMsList.Average() : 0,
                    MedianWriteMs = GetPercentile(writeMsList, 0.50),
                    P95WriteMs = GetPercentile(writeMsList, 0.95),
                    P99WriteMs = GetPercentile(writeMsList, 0.99),
                    MaxWriteMs = writeMsList.Count > 0 ? writeMsList.Max() : 0,
                    WriteCount250ms = writeMsList.Count(w => w >= 250),
                    WriteCount500ms = writeMsList.Count(w => w >= 500),
                    WriteCount1000ms = writeMsList.Count(w => w >= 1000),
                    MaxNoCompletedWriteMs = maxGap
                };
                metrics.SlowWriteEvents.AddRange(slowWrites);
                return metrics;
            }
            finally
            {
                if (File.Exists(destPath))
                {
                    try { File.Delete(destPath); } catch { }
                }
            }
        }

        private async Task<ExtendedBenchmarkMetrics> RunTwoBufferPipelineTest(string configName, string sourcePath, string destDir, int appBufferSize)
        {
            string destPath = Path.Combine(destDir, $"bench_e_pipe_{appBufferSize}_{Guid.NewGuid():N}.dat");
            try
            {
                byte[] bufA = ArrayPool<byte>.Shared.Rent(appBufferSize);
                byte[] bufB = ArrayPool<byte>.Shared.Rent(appBufferSize);

                List<double> readMsList = new();
                List<double> writeMsList = new();
                List<double> writeCompTimesMs = new();
                List<(long byteOffset, double writeMs, double gapMs)> slowWrites = new();

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
                double lastCompMs = 0;

                try
                {
                    using var sourceStream = new FileStream(sourcePath, sourceOptions);
                    using var destStream = new FileStream(destPath, destOptions);

                    byte[] currentWriteBuf = bufA;
                    byte[] currentReadBuf = bufB;

                    long r0 = Stopwatch.GetTimestamp();
                    int bytesRead = await sourceStream.ReadAsync(currentWriteBuf.AsMemory(0, appBufferSize));
                    long r1 = Stopwatch.GetTimestamp();
                    readMsList.Add((r1 - r0) * 1000.0 / Stopwatch.Frequency);

                    while (bytesRead > 0)
                    {
                        int bytesToWrite = bytesRead;

                        Task<int> nextReadTask = sourceStream.ReadAsync(currentReadBuf.AsMemory(0, appBufferSize)).AsTask();

                        long w0 = Stopwatch.GetTimestamp();
                        await destStream.WriteAsync(currentWriteBuf.AsMemory(0, bytesToWrite));
                        long w1 = Stopwatch.GetTimestamp();
                        double writeMs = (w1 - w0) * 1000.0 / Stopwatch.Frequency;
                        writeMsList.Add(writeMs);

                        double compMs = (w1 - overallStart) * 1000.0 / Stopwatch.Frequency;
                        writeCompTimesMs.Add(compMs);
                        double gap = compMs - lastCompMs;
                        lastCompMs = compMs;

                        if (writeMs >= 250)
                        {
                            slowWrites.Add((totalBytes, writeMs, gap));
                        }

                        totalBytes += bytesToWrite;

                        long rStart = Stopwatch.GetTimestamp();
                        bytesRead = await nextReadTask;
                        long rEnd = Stopwatch.GetTimestamp();
                        readMsList.Add((rEnd - rStart) * 1000.0 / Stopwatch.Frequency);

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

                double maxGap = 0;
                double prevComp = 0;
                foreach (var c in writeCompTimesMs)
                {
                    double gap = c - prevComp;
                    if (gap > maxGap) maxGap = gap;
                    prevComp = c;
                }

                var metrics = new ExtendedBenchmarkMetrics
                {
                    ConfigName = configName,
                    AppBufferSize = appBufferSize,
                    TotalBytes = totalBytes,
                    ElapsedSeconds = totalSec,
                    AverageMiBps = (totalBytes / 1048576.0) / totalSec,
                    ReadOps = readMsList.Count,
                    WriteOps = writeMsList.Count,
                    AvgReadMs = readMsList.Count > 0 ? readMsList.Average() : 0,
                    P95ReadMs = GetPercentile(readMsList, 0.95),
                    P99ReadMs = GetPercentile(readMsList, 0.99),
                    MaxReadMs = readMsList.Count > 0 ? readMsList.Max() : 0,
                    AvgWriteMs = writeMsList.Count > 0 ? writeMsList.Average() : 0,
                    MedianWriteMs = GetPercentile(writeMsList, 0.50),
                    P95WriteMs = GetPercentile(writeMsList, 0.95),
                    P99WriteMs = GetPercentile(writeMsList, 0.99),
                    MaxWriteMs = writeMsList.Count > 0 ? writeMsList.Max() : 0,
                    WriteCount250ms = writeMsList.Count(w => w >= 250),
                    WriteCount500ms = writeMsList.Count(w => w >= 500),
                    WriteCount1000ms = writeMsList.Count(w => w >= 1000),
                    MaxNoCompletedWriteMs = maxGap
                };
                metrics.SlowWriteEvents.AddRange(slowWrites);
                return metrics;
            }
            finally
            {
                if (File.Exists(destPath))
                {
                    try { File.Delete(destPath); } catch { }
                }
            }
        }

        private static (double miBps, double seconds) RunRobocopyTest(string sourcePath, string destDir)
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
        public async Task ExecuteRealEDriveABBenchmark()
        {
            string sourcePath = GetSourceFilePath();
            string destDir = GetEDriveDestinationDir();
            long fileSize = new FileInfo(sourcePath).Length;

            _output.WriteLine($"E:\\ DRIVE BENCHMARK SOURCE: {sourcePath} ({fileSize / (1024.0 * 1024.0):N2} MiB)");
            _output.WriteLine($"E:\\ DRIVE BENCHMARK DESTINATION: {destDir}");
            _output.WriteLine(new string('=', 90));

            // Config A: 4 MiB Single-Buffer
            _output.WriteLine("Running Config A: 4 MiB Single-Buffer on E:\\...");
            var resA = await RunSingleBufferTest("A) 4 MiB Single-Buffer", sourcePath, destDir, 4 * 1024 * 1024);

            // Config B: 512 KiB Single-Buffer
            _output.WriteLine("Running Config B: 512 KiB Single-Buffer on E:\\...");
            var resB = await RunSingleBufferTest("B) 512 KiB Single-Buffer", sourcePath, destDir, 512 * 1024);

            // Config C: 512 KiB Bounded 2-Buffer Pipeline
            _output.WriteLine("Running Config C: 512 KiB Bounded 2-Buffer Pipeline on E:\\...");
            var resC = await RunTwoBufferPipelineTest("C) 512 KiB 2-Buffer Pipeline", sourcePath, destDir, 512 * 1024);

            // Robocopy Control
            _output.WriteLine("Running Robocopy Control on E:\\...");
            var (roboMiBps, roboSec) = RunRobocopyTest(sourcePath, destDir);
            _output.WriteLine($"Robocopy on E:\\: {roboMiBps:N2} MiB/s ({roboSec:N2} sec)");
            _output.WriteLine(new string('=', 90));

            var allResults = new List<ExtendedBenchmarkMetrics> { resA, resB, resC };

            // Detailed Output per config
            foreach (var r in allResults)
            {
                _output.WriteLine($"\nCONFIG DETAILS: {r.ConfigName}");
                _output.WriteLine($"Total Bytes: {r.TotalBytes:N0} | Elapsed: {r.ElapsedSeconds:N2} s | Throughput: {r.AverageMiBps:N2} MiB/s");
                _output.WriteLine($"Read Ops: {r.ReadOps} | Write Ops: {r.WriteOps}");
                _output.WriteLine($"ReadAsync (ms): Avg={r.AvgReadMs:N2}, P95={r.P95ReadMs:N2}, P99={r.P99ReadMs:N2}, Max={r.MaxReadMs:N2}");
                _output.WriteLine($"WriteAsync (ms): Avg={r.AvgWriteMs:N2}, Med={r.MedianWriteMs:N2}, P95={r.P95WriteMs:N2}, P99={r.P99WriteMs:N2}, Max={r.MaxWriteMs:N2}");
                _output.WriteLine($"Slow Write Counts: >=250ms={r.WriteCount250ms}, >=500ms={r.WriteCount500ms}, >=1000ms={r.WriteCount1000ms}");
                _output.WriteLine($"Max No-Completed-Write Interval: {r.MaxNoCompletedWriteMs:N1} ms");

                if (r.SlowWriteEvents.Count > 0)
                {
                    _output.WriteLine("Slow Write Event Trace:");
                    for (int i = 0; i < Math.Min(10, r.SlowWriteEvents.Count); i++)
                    {
                        var e = r.SlowWriteEvents[i];
                        _output.WriteLine($"  [{i+1}] At ByteOffset {e.byteOffset:N0} ({e.byteOffset / (1024.0*1024.0):N1} MB): WriteMs={e.writeMs:N1} ms, GapMs={e.gapMs:N1} ms");
                    }
                }
                else
                {
                    _output.WriteLine("Slow Write Event Trace: None (0 writes >= 250 ms)");
                }
            }

            // Summary Table
            _output.WriteLine("\nE:\\ BENCHMARK SUMMARY TABLE:");
            _output.WriteLine(string.Format("{0,-32} | {1,8} | {2,8} | {3,8} | {4,8} | {5,8} | {6,8} | {7,8} | {8,8} | {9,8}",
                "Config", "Throughput", "Max Write", "Avg Write", "Med Write", "P95 Write", ">=250ms", ">=500ms", ">=1000ms", "Max NoProg"));
            _output.WriteLine(new string('-', 125));

            foreach (var r in allResults)
            {
                _output.WriteLine(string.Format("{0,-32} | {1,6:N2} MB/s | {2,6:N1} ms | {3,6:N1} ms | {4,6:N1} ms | {5,6:N1} ms | {6,8} | {7,8} | {8,8} | {9,6:N1} ms",
                    r.ConfigName,
                    r.AverageMiBps,
                    r.MaxWriteMs,
                    r.AvgWriteMs,
                    r.MedianWriteMs,
                    r.P95WriteMs,
                    r.WriteCount250ms,
                    r.WriteCount500ms,
                    r.WriteCount1000ms,
                    r.MaxNoCompletedWriteMs));
            }

            _output.WriteLine($"\nROBOCOPY ON E:\\ THROUGHPUT: {roboMiBps:N2} MiB/s ({roboSec:N2} s)");
        }
    }
}
