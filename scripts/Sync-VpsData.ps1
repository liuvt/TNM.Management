param(
    [string]$VpsHost = "103.118.29.100",
    [int]$VpsPort = 22,
    [string]$VpsUser = "root",
    [string]$VpsPass = $env:TNM_VPS_PASS,
    [string]$RemoteDbPath = "/var/www/tnm-datas/tnm-demo.db"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$AppDataDir = Join-Path $ProjectRoot "App_Data"
$BackupDir = Join-Path $AppDataDir "backups"
$LocalDbPath = Join-Path $AppDataDir "tnm-demo.db"

New-Item -ItemType Directory -Force -Path $AppDataDir | Out-Null
New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null

if (Test-Path $LocalDbPath) {
    $ts = Get-Date -Format "yyyyMMdd-HHmmss"
    $backupFile = Join-Path $BackupDir "tnm-local-before-sync-$ts.db"
    Copy-Item $LocalDbPath $backupFile -Force
    Write-Host "[1/4] Backed up local DB -> $backupFile" -ForegroundColor Cyan
}

$askPassBat = $null
if (![string]::IsNullOrWhiteSpace($VpsPass)) {
    $askPassBat = Join-Path $env:TEMP "tnm_ssh_askpass.bat"
    Set-Content -Path $askPassBat -Value "@echo off`r`necho $VpsPass" -Encoding ASCII
    $env:SSH_ASKPASS = $askPassBat
    $env:SSH_ASKPASS_REQUIRE = "force"
    $env:DISPLAY = "1"
}

try {
    $remoteTemp = "/tmp/tnm-vps-snapshot.db"
    Write-Host "[2/4] Creating consistent SQLite snapshot on VPS ($VpsHost`:$VpsPort)..." -ForegroundColor Cyan
    & ssh.exe -p $VpsPort -o StrictHostKeyChecking=no -o ConnectTimeout=10 "$VpsUser@$VpsHost" "sqlite3 $RemoteDbPath '.backup $remoteTemp' && ls -lh $remoteTemp"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create SQLite snapshot on VPS (exit code $LASTEXITCODE)."
    }

    $downloadedTemp = Join-Path $AppDataDir "tnm-demo.vps-download.db"
    Write-Host "[3/4] Downloading snapshot via SCP..." -ForegroundColor Cyan
    & scp.exe -P $VpsPort -o StrictHostKeyChecking=no -o ConnectTimeout=10 "$VpsUser@${VpsHost}:$remoteTemp" $downloadedTemp
    if ($LASTEXITCODE -ne 0 -or !(Test-Path $downloadedTemp)) {
        throw "Failed to download database snapshot from VPS."
    }

    Remove-Item "$LocalDbPath-wal" -ErrorAction SilentlyContinue
    Remove-Item "$LocalDbPath-shm" -ErrorAction SilentlyContinue
    Move-Item $downloadedTemp $LocalDbPath -Force

    & ssh.exe -p $VpsPort -o StrictHostKeyChecking=no -o ConnectTimeout=10 "$VpsUser@$VpsHost" "rm -f $remoteTemp" | Out-Null
    Write-Host "[4/4] Successfully synced VPS database to $LocalDbPath" -ForegroundColor Green
}
finally {
    if ($askPassBat -and (Test-Path $askPassBat)) {
        Remove-Item $askPassBat -ErrorAction SilentlyContinue
    }
}
