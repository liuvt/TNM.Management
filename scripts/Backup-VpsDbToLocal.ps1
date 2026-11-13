param(
    [string]$VpsHost = "100.72.199.68",
    [int]$VpsPort = 22,
    [string]$VpsUser = "root",
    [string]$VpsPass = $env:TNM_VPS_PASS,
    [string]$RemoteDataDir = "/var/www/tnm-datas"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$LocalAppData = Join-Path $ProjectRoot "App_Data"
$LocalBackupDir = Join-Path $LocalAppData "backups"
New-Item -ItemType Directory -Force -Path $LocalBackupDir | Out-Null

$ts = Get-Date -Format "yyyyMMdd-HHmmss"
$LocalDbPath = Join-Path $LocalAppData "tnm-local.db"
$LocalArchivePath = Join-Path $LocalBackupDir "tnm-vps-$ts.db"

if (Test-Path $LocalDbPath) {
    $PreSyncPath = Join-Path $LocalBackupDir "tnm-local-before-sync-$ts.db"
    Copy-Item -Path $LocalDbPath -Destination $PreSyncPath -Force
    Write-Host "[1/4] Backed up existing local DB to: $PreSyncPath" -ForegroundColor DarkGray
}

$askPassBat = $null
if (![string]::IsNullOrWhiteSpace($VpsPass)) {
    $askPassBat = Join-Path $env:TEMP "tnm_ssh_askpass_backup.bat"
    Set-Content -Path $askPassBat -Value "@echo off`r`necho $VpsPass" -Encoding ASCII
    $env:SSH_ASKPASS = $askPassBat
    $env:SSH_ASKPASS_REQUIRE = "force"
    $env:DISPLAY = "1"
}

try {
    Write-Host "[2/4] Creating hot SQLite backup snapshot on VPS ($VpsHost)..." -ForegroundColor Cyan
    $remoteCmd = "sqlite3 $RemoteDataDir/tnm-local.db '.backup /tmp/tnm-vps-latest.db' && ls -lh /tmp/tnm-vps-latest.db"
    & ssh.exe -p $VpsPort -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ConnectTimeout=10 "$VpsUser@$VpsHost" $remoteCmd
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create SQLite backup on VPS (exit code $LASTEXITCODE)."
    }

    Write-Host "[3/4] Downloading VPS database to local..." -ForegroundColor Cyan
    & scp.exe -P $VpsPort -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ConnectTimeout=10 "$VpsUser@${VpsHost}:/tmp/tnm-vps-latest.db" $LocalArchivePath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to download database from VPS (exit code $LASTEXITCODE)."
    }

    & ssh.exe -p $VpsPort -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ConnectTimeout=10 "$VpsUser@$VpsHost" "rm -f /tmp/tnm-vps-latest.db"

    # Remove stale WAL/SHM files before replacing local DB
    Remove-Item "$LocalDbPath-wal" -ErrorAction SilentlyContinue
    Remove-Item "$LocalDbPath-shm" -ErrorAction SilentlyContinue
    Copy-Item -Path $LocalArchivePath -Destination $LocalDbPath -Force

    Write-Host "[4/4] Successfully synced VPS database to local!" -ForegroundColor Green
    Write-Host "  -> Active Local DB : $LocalDbPath" -ForegroundColor Green
    Write-Host "  -> Backup Archive  : $LocalArchivePath" -ForegroundColor Green
}
finally {
    if ($askPassBat -and (Test-Path $askPassBat)) {
        Remove-Item $askPassBat -ErrorAction SilentlyContinue
    }
}
