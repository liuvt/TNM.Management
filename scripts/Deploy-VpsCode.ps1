param(
    [string]$VpsHost = "103.118.29.100",
    [int]$VpsPort = 22,
    [string]$VpsUser = "root",
    [string]$VpsPass = $env:TNM_VPS_PASS,
    [string]$RemoteAppDir = "/var/www/tnm-management",
    [string]$RemoteDataDir = "/var/www/tnm-datas"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ArchivePath = Join-Path $ProjectRoot "tnm-linux-publish.zip"

Write-Host "[1/4] Building Linux publish package..." -ForegroundColor Cyan
& powershell -ExecutionPolicy Bypass -File (Join-Path $ProjectRoot "deploy\linux\Publish-TNM-Linux.ps1")

$askPassBat = $null
if (![string]::IsNullOrWhiteSpace($VpsPass)) {
    $askPassBat = Join-Path $env:TEMP "tnm_ssh_askpass_deploy.bat"
    Set-Content -Path $askPassBat -Value "@echo off`r`necho $VpsPass" -Encoding ASCII
    $env:SSH_ASKPASS = $askPassBat
    $env:SSH_ASKPASS_REQUIRE = "force"
    $env:DISPLAY = "1"
}

try {
    Write-Host "[2/4] Uploading tnm-linux-publish.zip to $VpsHost`:$VpsPort..." -ForegroundColor Cyan
    & scp.exe -P $VpsPort -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ConnectTimeout=10 $ArchivePath "$VpsUser@${VpsHost}:/tmp/tnm-linux-publish.zip"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to upload package to VPS (exit code $LASTEXITCODE). Check if SSH port $VpsPort is open."
    }

    Write-Host "[3/4] Updating code on VPS (preserving database in $RemoteDataDir)..." -ForegroundColor Cyan
    $remoteCmd = @"
set -e
mkdir -p $RemoteDataDir/backups
if [ -f $RemoteDataDir/tnm-demo.db ]; then
    sqlite3 $RemoteDataDir/tnm-demo.db ".backup $RemoteDataDir/backups/tnm-before-deploy-`$(date +%Y%m%d-%H%M%S).db"
fi
systemctl stop tnm
find $RemoteAppDir -mindepth 1 -maxdepth 1 ! -name 'App_Data' -exec rm -rf {} +
unzip -q -o /tmp/tnm-linux-publish.zip -d $RemoteAppDir || true
rm -f /tmp/tnm-linux-publish.zip
ln -sfn $RemoteDataDir $RemoteAppDir/App_Data
chown -R root:tnm $RemoteAppDir
find $RemoteAppDir -type d -exec chmod 750 {} +
find $RemoteAppDir -type f -exec chmod 640 {} +
chown -R tnm:tnm $RemoteDataDir
systemctl start tnm
sleep 2
systemctl is-active tnm
"@ -replace "`r", ""

    & ssh.exe -p $VpsPort -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ConnectTimeout=10 "$VpsUser@$VpsHost" $remoteCmd
    if ($LASTEXITCODE -ne 0) {
        throw "Remote deployment command failed (exit code $LASTEXITCODE)."
    }

    Write-Host "[4/4] Deployed code successfully to VPS without touching database!" -ForegroundColor Green
}
finally {
    if ($askPassBat -and (Test-Path $askPassBat)) {
        Remove-Item $askPassBat -ErrorAction SilentlyContinue
    }
}
