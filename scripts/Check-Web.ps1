$ErrorActionPreference = 'Continue'
Write-Host 'HTTP /account/login (may chu local):'
try {
    $r = Invoke-WebRequest -Uri 'http://127.0.0.1:5236/account/login' -UseBasicParsing -MaximumRedirection 3 -TimeoutSec 8
    Write-Host "HTTP: $($r.StatusCode) - $($r.BaseResponse.ResponseUri)"
    if ($r.Content -match 'Đăng nhập hệ thống') { Write-Host 'OK: Trang login da tra ve HTML.' }
} catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    Write-Host 'Kiem tra ung dung dang chay o PowerShell khac va cong 5236.'
}
