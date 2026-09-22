using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScheduledCopyManager.Domain.Enums;
using ScheduledCopyManager.Domain.Interfaces;
using ScheduledCopyManager.Domain.Models;
using ScheduledCopyManager.Infrastructure.Repositories;
using ScheduledCopyManager.Infrastructure.Services;
using ScheduledCopyManager.Presentation.Services;
using ScheduledCopyManager.Presentation.ViewModels;
using Xunit;

namespace ScheduledCopyManager.Tests
{
    public class Phase3AsyncPauseProgressRegressionTests : IDisposable
    {
        private readonly string _testDir;

        public Phase3AsyncPauseProgressRegressionTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "Phase3v5Tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
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

        private class SingleThreadSynchronizationContext : SynchronizationContext
        {
            private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
            private readonly Thread _thread;
            public bool RanTasks { get; private set; }

            public SingleThreadSynchronizationContext()
            {
                _thread = new Thread(Run) { IsBackground = true };
                _thread.Start();
            }

            private void Run()
            {
                SetSynchronizationContext(this);
                foreach (var item in _queue.GetConsumingEnumerable())
                {
                    RanTasks = true;
                    item.Callback(item.State);
                }
            }

            public override void Post(SendOrPostCallback d, object? state)
            {
                _queue.Add((d, state));
            }

            public override void Send(SendOrPostCallback d, object? state)
            {
                throw new InvalidOperationException("Send (synchronous invoke) was called on SynchronizationContext!");
            }

            public void Complete()
            {
                _queue.CompleteAdding();
                _thread.Join(1000);
            }
        }

        [Fact]
        public async Task PauseJobAsync_DoesNotBlockSynchronizationContext()
        {
            var repo = new CheckpointRepository(customDirectory: _testDir);
            var manager = new JobExecutionManager(checkpointRepository: repo);
            var job = new Job { Id = Guid.NewGuid(), Name = "TestPause" };

            var session = manager.RegisterSession(job);

            // Create initial checkpoint
            var cp = new JobCheckpoint
            {
                JobId = job.Id,
                JobName = job.Name,
                CurrentState = ExecutionState.Running
            };
            await repo.SaveCheckpointAsync(cp);

            var prevSyncContext = SynchronizationContext.Current;
            var testSyncContext = new SingleThreadSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(testSyncContext);

            try
            {
                // PauseJobAsync must complete cleanly without deadlocking on SynchronizationContext
                bool result = await manager.PauseJobAsync(job.Id);
                Assert.True(result);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevSyncContext);
                testSyncContext.Complete();
            }

            var updatedCp = await repo.GetCheckpointAsync(job.Id);
            Assert.NotNull(updatedCp);
            Assert.Equal(ExecutionState.Paused, updatedCp.CurrentState);
            Assert.Equal(ExecutionInterruptionReason.UserPaused, updatedCp.InterruptionReasonCode);
        }

        [Fact]
        public async Task StopJobAsync_DoesNotBlockSynchronizationContext()
        {
            var repo = new CheckpointRepository(customDirectory: _testDir);
            var manager = new JobExecutionManager(checkpointRepository: repo);
            var job = new Job { Id = Guid.NewGuid(), Name = "TestStop" };

            var session = manager.RegisterSession(job);

            var cp = new JobCheckpoint
            {
                JobId = job.Id,
                JobName = job.Name,
                CurrentState = ExecutionState.Running
            };
            await repo.SaveCheckpointAsync(cp);

            var prevSyncContext = SynchronizationContext.Current;
            var testSyncContext = new SingleThreadSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(testSyncContext);

            try
            {
                bool result = await manager.StopJobAsync(job.Id);
                Assert.True(result);
                Assert.True(session.CancellationTokenSource.IsCancellationRequested);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevSyncContext);
                testSyncContext.Complete();
            }

            var updatedCp = await repo.GetCheckpointAsync(job.Id);
            Assert.NotNull(updatedCp);
            Assert.Equal(ExecutionState.Stopped, updatedCp.CurrentState);
            Assert.Equal(ExecutionInterruptionReason.UserStopped, updatedCp.InterruptionReasonCode);
        }

        [Fact]
        public async Task Pause_PersistsUserPaused_BeforeCommandCompletion()
        {
            var repo = new CheckpointRepository(customDirectory: _testDir);
            var manager = new JobExecutionManager(checkpointRepository: repo);
            var job = new Job { Id = Guid.NewGuid(), Name = "TestPausePersistence" };

            var session = manager.RegisterSession(job);
            await repo.SaveCheckpointAsync(new JobCheckpoint { JobId = job.Id, JobName = job.Name, CurrentState = ExecutionState.Running });

            bool pauseResult = await manager.PauseJobAsync(job.Id);
            Assert.True(pauseResult);

            // Immediately check persisted state right after pause completes
            var savedCp = await repo.GetCheckpointAsync(job.Id);
            Assert.NotNull(savedCp);
            Assert.Equal(ExecutionState.Paused, savedCp.CurrentState);
            Assert.Equal(ExecutionInterruptionReason.UserPaused, savedCp.InterruptionReasonCode);
        }

        [Fact]
        public async Task Stop_PersistsUserStopped_BeforeCancellation()
        {
            var repo = new CheckpointRepository(customDirectory: _testDir);
            var manager = new JobExecutionManager(checkpointRepository: repo);
            var job = new Job { Id = Guid.NewGuid(), Name = "TestStopPersistence" };

            var session = manager.RegisterSession(job);
            await repo.SaveCheckpointAsync(new JobCheckpoint { JobId = job.Id, JobName = job.Name, CurrentState = ExecutionState.Running });

            bool stopResult = await manager.StopJobAsync(job.Id);
            Assert.True(stopResult);

            var savedCp = await repo.GetCheckpointAsync(job.Id);
            Assert.NotNull(savedCp);
            Assert.Equal(ExecutionState.Stopped, savedCp.CurrentState);
            Assert.Equal(ExecutionInterruptionReason.UserStopped, savedCp.InterruptionReasonCode);
            Assert.True(manager.IsJobStoppedByUser(job.Id));
        }

        [Fact]
        public async Task RestartAfterPause_StillRequiresExplicitRecoveryResume()
        {
            var repo = new CheckpointRepository(customDirectory: _testDir);
            var manager = new JobExecutionManager(checkpointRepository: repo);
            var job = new Job { Id = Guid.NewGuid(), Name = "RestartPauseTest" };

            var session = manager.RegisterSession(job);
            await repo.SaveCheckpointAsync(new JobCheckpoint { JobId = job.Id, JobName = job.Name, CurrentState = ExecutionState.Running });

            await manager.PauseJobAsync(job.Id);

            // Simulate application restart with new gate
            var newGate = new JobExecutionGate(repo);
            var resultWithoutAuth = await newGate.CanExecuteAsync(job.Id, ExecutionTriggerSource.QuartzScheduled);
            Assert.False(resultWithoutAuth.Allowed);

            var resultWithAuth = await newGate.CanExecuteAsync(job.Id, ExecutionTriggerSource.RecoveryResume);
            Assert.True(resultWithAuth.Allowed);
        }

        [Fact]
        public async Task RestartAfterStop_StillRequiresExplicitRecoveryResume()
        {
            var repo = new CheckpointRepository(customDirectory: _testDir);
            var manager = new JobExecutionManager(checkpointRepository: repo);
            var job = new Job { Id = Guid.NewGuid(), Name = "RestartStopTest" };

            var session = manager.RegisterSession(job);
            await repo.SaveCheckpointAsync(new JobCheckpoint { JobId = job.Id, JobName = job.Name, CurrentState = ExecutionState.Running });

            await manager.StopJobAsync(job.Id);

            var newGate = new JobExecutionGate(repo);
            var resultWithoutAuth = await newGate.CanExecuteAsync(job.Id, ExecutionTriggerSource.QuartzScheduled);
            Assert.False(resultWithoutAuth.Allowed);

            var resultWithAuth = await newGate.CanExecuteAsync(job.Id, ExecutionTriggerSource.RecoveryResume);
            Assert.True(resultWithAuth.Allowed);
        }
    }
}
