# Run in PowerShell on a Windows PC with .NET 9 SDK.
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$project = Join-Path $projectRoot 'TNM.Management.csproj'
$publish = Join-Path $projectRoot 'publish-linux'
$archive = Join-Path $projectRoot 'tnm-linux-publish.zip'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $archive) { Remove-Item $archive -Force }
dotnet publish $project -c Release -r linux-x64 --self-contained false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
# Include deployment assets explicitly: ordinary files under deploy/linux are not
# guaranteed to be copied by dotnet publish for a Web SDK project.
$deployDest = Join-Path $publish 'deploy/linux'
New-Item -ItemType Directory -Path $deployDest -Force | Out-Null
foreach ($name in @('tnm.service', 'nginx-tnm.conf', 'backup-tnm.sh', 'README-LINUX.md')) {
    Copy-Item (Join-Path $PSScriptRoot $name) (Join-Path $deployDest $name) -Force
}
# Ensure SQL schema and seeds exist alongside published DLL, regardless of
# project content-copy implementation details.
$sqlDest = Join-Path $publish 'Data'
New-Item -ItemType Directory -Path $sqlDest -Force | Out-Null
Copy-Item (Join-Path $projectRoot 'Data/*.sql') $sqlDest -Force
foreach ($required in @('TNM.Management.dll', 'Data/schema.sql', 'Data/seed_empty.sql', 'Data/seed.sql', 'deploy/linux/tnm.service')) {
    if (-not (Test-Path (Join-Path $publish $required))) {
        throw "Missing published asset: $required"
    }
}

# Never transfer any local database, SQLite WAL or cookie encryption keys.
$localData = Join-Path $publish 'App_Data'
if (Test-Path $localData) { Remove-Item $localData -Recurse -Force }
Push-Location $projectRoot
try {
    tar.exe -a -c -f "tnm-linux-publish.zip" -C "publish-linux" *
    if ($LASTEXITCODE -ne 0) { Compress-Archive -Path "$publish/*" -DestinationPath $archive -Force }
}
finally {
    Pop-Location
}
Write-Host "Done: $archive"
