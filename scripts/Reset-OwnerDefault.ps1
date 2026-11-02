# Chay tai may chu, khi ung dung TNM da dung; sao luu App_Data truoc khi reset.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
Write-Host 'Dat lai owner ve mat khau mac dinh; buoc doi sau khi dang nhap.'
dotnet run --no-launch-profile -- --reset-owner-default
