# Chạy kiểm thử tự động toàn bộ luồng nghiệp vụ TNM.Management V7
$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $ProjectRoot
try {
    dotnet run -- --test-flows
    if ($LASTEXITCODE -ne 0) {
        throw "Kiểm thử luồng thất bại (ExitCode=$LASTEXITCODE)."
    }
}
finally {
    Pop-Location
}
