#!/usr/bin/env bash
# restore.sh <env> <backup-file-name> — restores a backup from /opt/mypos/<env>/backups (stops the API meanwhile).
source "$(dirname "$0")/lib.sh"
need_env "${1:-}"; e=$1; file=${2:?backup file name, see: ls /opt/mypos/$1/backups}
db=$(getenv "$e" DB_NAME); pw=$(getenv "$e" DB_SA_PASSWORD)
[[ -f "$ROOT/$e/backups/$file" ]] || { echo "No such backup: $file" >&2; exit 1; }
dc "$e" stop api
dc "$e" exec -T db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$pw" -b -Q \
  "IF DB_ID(N'$db') IS NOT NULL ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [$db] FROM DISK = N'/backups/$file' WITH REPLACE; ALTER DATABASE [$db] SET MULTI_USER;"
dc "$e" start api
health "$e" 120 && log "Restored $file into $db." || { log "Restored, but health check failed."; exit 1; }
