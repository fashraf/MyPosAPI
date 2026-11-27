#!/usr/bin/env bash
# backup.sh <env> [label] — full, compressed, verified database backup into /opt/mypos/<env>/backups (keeps 14).
source "$(dirname "$0")/lib.sh"
need_env "${1:-}"; e=$1; label=${2:-manual}
db=$(getenv "$e" DB_NAME); pw=$(getenv "$e" DB_SA_PASSWORD)
mkdir -p "$ROOT/$e/backups"
dc "$e" up -d db >/dev/null
for _ in $(seq 1 40); do dc "$e" exec -T db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$pw" -Q "SELECT 1" -b -o /dev/null 2>/dev/null && break; sleep 3; done
exists=$(dc "$e" exec -T db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$pw" -h -1 -W -Q "SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'$db') IS NULL THEN 0 ELSE 1 END" | tr -dc '01')
if [[ "$exists" != "1" ]]; then log "No database yet — nothing to back up."; exit 0; fi
file="${db}-$(date -u +%Y%m%d-%H%M%S)-${label//[^A-Za-z0-9._-]/_}.bak"
log "Backing up $db -> backups/$file"
dc "$e" exec -T db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$pw" -b -Q \
  "BACKUP DATABASE [$db] TO DISK = N'/backups/$file' WITH COPY_ONLY, INIT, CHECKSUM; RESTORE VERIFYONLY FROM DISK = N'/backups/$file' WITH CHECKSUM;"
ls -1t "$ROOT/$e/backups"/*.bak 2>/dev/null | tail -n +15 | xargs -r rm -f
log "Backup OK: $file"
