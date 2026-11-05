# Chỉ dùng trên máy chủ TNM; KHÔNG xóa database.
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$db = Join-Path $projectRoot 'App_Data\tnm-local.db'
if (-not (Test-Path -LiteralPath $db)) { throw "Không thấy database: $db" }
Write-Host 'Dừng ứng dụng TNM trước khi đặt lại mật khẩu.' -ForegroundColor Yellow
Write-Host 'Mật khẩu nhập tại đây không được hiển thị hoặc ghi vào lệnh PowerShell.'
Push-Location $projectRoot
try {
    dotnet run --no-launch-profile -- --reset-owner-password
    if ($LASTEXITCODE -ne 0) { throw "Reset thất bại. Exit code: $LASTEXITCODE" }
} finally { Pop-Location }
