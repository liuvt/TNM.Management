# Requires Python 3; uses SQLite online backup API, safe to run while the web app is serving.
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$source = Join-Path $project 'App_Data/tnm-local.db'
if (!(Test-Path $source)) { throw "SQLite database not found: $source" }
$backupDir = Join-Path $project 'Backups'
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
$dest = Join-Path $backupDir ("tnm-v6-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.sqlite')
$script = "import sqlite3,sys`nsrc=sqlite3.connect(sys.argv[1])`ndst=sqlite3.connect(sys.argv[2])`nsrc.backup(dst)`nassert dst.execute('pragma integrity_check').fetchone()[0]=='ok'`ndst.close()`nsrc.close()`n"
$script | python - $source $dest
if ($LASTEXITCODE -ne 0) { throw 'SQLite backup failed' }
Write-Host "Backup validated: $dest"
