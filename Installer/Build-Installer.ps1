# Build-Installer.ps1 for Terabithia Sync
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$solutionDir = (Resolve-Path "$scriptDir\..").Path

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " TERABITHIA SYNC BUILD & PACKAGING SCRIPT " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

Set-Location $solutionDir

# 0. Clean Previous Build Output and stop background processes
Write-Host "`n[0/6] Cleaning Previous Build & Publish Output..." -ForegroundColor Yellow
Get-Process TerabithiaSync, ISCC -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if (Test-Path "ScheduledCopyManager.App\bin") { Remove-Item "ScheduledCopyManager.App\bin" -Recurse -Force }
if (Test-Path "InstallerOutput") { Remove-Item "InstallerOutput" -Recurse -Force }
if (Test-Path "Release") { Remove-Item "Release" -Recurse -Force }

# 1. Restore Solution
Write-Host "`n[1/6] Restoring Solution..." -ForegroundColor Yellow
dotnet restore
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet restore failed!"
    exit 1
}

# 2. Build Release Configuration
Write-Host "`n[2/6] Building Solution (Release)..." -ForegroundColor Yellow
dotnet build -c Release --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet build failed!"
    exit 1
}

# 3. Run Automated Tests
Write-Host "`n[3/6] Running Unit Tests..." -ForegroundColor Yellow
dotnet test -c Release --no-build
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet test failed!"
    exit 1
}

# 4. Publish Self-Contained Application (win-x64 --self-contained true)
Write-Host "`n[4/6] Publishing Self-Contained Application (win-x64)..." -ForegroundColor Yellow
dotnet publish ScheduledCopyManager.App\ScheduledCopyManager.App.csproj -c Release -r win-x64 --self-contained true
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed!"
    exit 1
}

$publishDir = (Resolve-Path "ScheduledCopyManager.App\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish").Path
$exePath = Join-Path $publishDir "TerabithiaSync.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Published executable not found at: $exePath"
    exit 1
}

# Verify runtime files exist in publish directory (CLR host, coreclr.dll, etc.)
if (-not (Test-Path (Join-Path $publishDir "coreclr.dll"))) {
    Write-Error "Self-contained publish verification failed: coreclr.dll missing!"
    exit 1
}

Write-Host "Self-contained publish verified! Executable: $exePath" -ForegroundColor Green

# 5. Locate Inno Setup Compiler (ISCC.exe)
Write-Host "`n[5/6] Locating Inno Setup Compiler (ISCC.exe)..." -ForegroundColor Yellow

$isccPath = $null
$possiblePaths = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 5\ISCC.exe",
    "C:\Program Files\Inno Setup 5\ISCC.exe"
)

foreach ($path in $possiblePaths) {
    if (Test-Path $path) {
        $isccPath = $path
        break
    }
}

if (-not $isccPath) {
    $commandInPath = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($commandInPath) {
        $isccPath = $commandInPath.Source
    }
}

if (-not $isccPath) {
    Write-Error "ISCC.exe (Inno Setup Compiler) was not found."
    exit 1
}

Write-Host "ISCC.exe found at: $isccPath" -ForegroundColor Green

# 6. Compile Inno Setup Installer
Write-Host "`n[6/6] Compiling Installer..." -ForegroundColor Yellow
$issPath = (Resolve-Path "Installer\TerabithiaSync.iss").Path

& "$isccPath" "$issPath"

$outputInstaller = (Resolve-Path "InstallerOutput\TerabithiaSync-Setup-1.0.0.exe").Path
if (-not (Test-Path $outputInstaller)) {
    Write-Error "Installer executable not found after build!"
    exit 1
}

# Create Release folder structure explicitly
$releaseDir = Join-Path $solutionDir "Release"
$releaseAppDir = Join-Path $releaseDir "TerabithiaSync"
$releaseInstDir = Join-Path $releaseDir "Installer"

New-Item -ItemType Directory -Force -Path $releaseAppDir | Out-Null
New-Item -ItemType Directory -Force -Path $releaseInstDir | Out-Null

Copy-Item -Path "$publishDir\*" -Destination $releaseAppDir -Recurse -Force

# Safely copy installer to Release\Installer with retries to handle any temporary file locks
$destInstaller = Join-Path $releaseInstDir "TerabithiaSync-Setup-1.0.0.exe"
$copied = $false

for ($attempt = 1; $attempt -le 10; $attempt++) {
    try {
        [System.IO.File]::Copy($outputInstaller, $destInstaller, $true)
        $copied = $true
        break
    } catch {
        Start-Sleep -Milliseconds 500
    }
}

if (-not $copied) {
    Write-Error "Failed to copy installer executable to Release directory due to file lock."
    exit 1
}

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host " BUILD & PACKAGING COMPLETED SUCCESSFULLY! " -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host "EXECUTABLE: $exePath"
Write-Host "PUBLISH DIR: $publishDir"
Write-Host "INSTALLER: $destInstaller"

exit 0
