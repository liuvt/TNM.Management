#!/usr/bin/env bash
set -euo pipefail
DB=/opt/tnm/app/App_Data/tnm-local.db
DEST=/opt/tnm/backups
mkdir -p "$DEST"
chmod 700 "$DEST"
OUT="$DEST/tnm-$(date +%Y%m%d-%H%M%S).db"
# sqlite3 .backup captures a transactionally consistent online snapshot.
sqlite3 "$DB" ".backup '$OUT'"
chmod 600 "$OUT"
echo "SQLite backup: $OUT"
