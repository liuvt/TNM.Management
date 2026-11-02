param([switch]$Demo)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (-not (Test-Path 'App_Data/tnm-demo.db')) {
    $env:TNM_SEED_DEMO = if ($Demo) { '1' } else { '0' }
    Write-Host 'Tao SQLite moi va tai khoan owner; bat buoc doi mat khau sau dang nhap.'
} else {
    Write-Host 'Dung database hien co; khong dat lai mat khau owner.'
}
dotnet restore
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
dotnet build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
dotnet run --no-build --urls 'http://localhost:5236'
