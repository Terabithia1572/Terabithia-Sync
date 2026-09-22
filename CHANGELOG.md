# Changelog

All notable changes to **Terabithia Sync** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to Semantic Versioning.

---

## [1.0.0-phase3-v14.2] - 2026-09-22

**BuildId:** `1.0.0-phase3-unified-retry-execution-v14.2`  
**Status:** Phase 3 Stable Release

### Added
- **Persistent Checkpoint & Recovery Engine**: Saves granular copy state to disk periodically, allowing interrupted jobs to resume without re-copying valid finished files.
- **Live Transfer UI Controls**: Real-time copy progress display (transferred bytes, current file, throughput speed, ETA, elapsed time) with interactive Pause, Stop, and Cancel controls.
- **File-Level History & Details Modal**: Detailed file-by-file status tracking (Copied, Skipped, Failed, Interrupted) with single-click modal details.
- **Centralized Retry Execution Pipeline**: Unified execution gate (`JobExecutionManager`) handling both fresh copy jobs and History Retry requests.
- **USB Volume Serial Identity Safety**: Captures Windows volume serial numbers for USB drives, resolving drive-letter remapping and blocking writes if an unexpected volume occupies a mapped drive letter.
- **USB Target Selector**: Direct USB removable volume dropdown selector in Job Editor.
- **Diagnostic Tools Page**: System diagnostics screen displaying connected USB drives, volume serials, filesystem types, storage capacity, and quick application data folder shortcuts.
- **Windows Notifications & System Tray Integration**: Native Windows Toast notifications for job completion, failure, and recovery alerts, alongside a dynamic System Tray icon.
- **Localization Support**: Full Turkish and English localization for all UI views, status labels, and Windows I/O error messages.
- **Duplicate Execution Protection**: Block competing execution requests for the same JobId across scheduled triggers, manual runs, and retry executions.

### Fixed
- **Startup Auto-Resume Recovery Regression**: Fixed issue where interrupted jobs would automatically execute upon application restart; now properly requires explicit user approval via the onscreen Recovery Banner.
- **Installer Packaging**: Resolved stale executable packaging issues in setup build scripts.
- **Timestamp Verification**: Fixed completed-file timestamp comparison logic during checkpoint recovery scans.
- **Pause Deadlock**: Fixed threading deadlock when pausing asynchronous file copy loops.
- **Phantom Active-Copy Card**: Resolved UI rendering glitch where completed jobs remained visible in active copy cards.
- **History Retry Execution**: Fixed issue where History Retry executed invisibly in the background without UI feedback or frozen viewmodels.
- **Same-Job Execution Guard**: Resolved race condition allowing duplicate concurrent executions of the same job.
- **USB Selector DI Registration**: Resolved dependency injection initialization bug in USB drive service components.
- **History Accounting Inconsistencies**: Fixed discrepancies between total file counts and copied/skipped/failed metrics in history reports.

### Performance
- **Tuned 512 KiB I/O Copy Buffer**: Production buffer size optimized to 512 KiB for high-throughput sequential file transfers on Windows.
- **Asynchronous Sequential File Streams**: Utilizes `FileOptions.Asynchronous | FileOptions.SequentialScan` for non-blocking disk operations.
- **Unthrottled Bandwidth Mode**: Unlimited bandwidth setting operates at maximum disk throughput without artificial delay.
- **Performance Diagnostic Benchmarks**: Added comprehensive automated benchmark test suite for I/O buffer validation.
