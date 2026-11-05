$ErrorActionPreference = 'Stop'
$db = Join-Path $PSScriptRoot '..\App_Data\tnm-local.db'
Write-Host 'Dừng web app trước khi reset. Reset sẽ xóa TOÀN BỘ dữ liệu demo.' -ForegroundColor Yellow
$answer = Read-Host 'Nhập RESET để tiếp tục'
if ($answer -ne 'RESET') { Write-Host 'Đã hủy.'; exit 0 }
foreach ($name in @($db, "$db-wal", "$db-shm")) {
  if (Test-Path $name) { Remove-Item -LiteralPath $name -Force }
}
Write-Host 'Đã xóa database demo. Chạy dotnet run để seed lại.'
