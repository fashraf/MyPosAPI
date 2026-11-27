#!/usr/bin/env bash
# migrate.sh <env> [--baseline]
# Applies, in order, every migration file shipped inside the API image that this database has not run yet.
# Journal: dbo.__Migrations (Name, Checksum, AppliedAtUtc). --baseline records all files as applied without running them
# (for a database that was set up by hand earlier).
source "$(dirname "$0")/lib.sh"
need_env "${1:-}"; e=$1; mode=${2:-}
db=$(getenv "$e" DB_NAME); pw=$(getenv "$e" DB_SA_PASSWORD)
app_user=$(getenv "$e" APP_DB_USER); app_pw=$(getenv "$e" APP_DB_PASSWORD)
image="$(getenv "$e" API_IMAGE):$(getenv "$e" API_TAG)"
sq() { dc "$e" exec -T db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$pw" -b -I -x "$@"; }

dc "$e" up -d db >/dev/null
for _ in $(seq 1 60); do sq -Q "SELECT 1" -o /dev/null 2>/dev/null && break; sleep 3; done

tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
cid=$(docker create "$image"); docker cp "$cid:/app/db-migrations/." "$tmp/"; docker rm "$cid" >/dev/null

# Order: the base schema, then the numbered files (11 … 29, 30 …). Diagnostics (00-*, 99-*) never run.
# Dev passwords (17-*) only on staging.
mapfile -t files < <(
  echo nazad-posmain-full.sql
  find "$tmp" -maxdepth 1 -name '[1-9][0-9]-*.sql' -printf '%f\n' | grep -v '^99-' | { if [[ "$e" == staging ]]; then cat; else grep -v '^17-dev-passwords'; fi; } | sort -V
)

sq -d master -Q "IF DB_ID(N'$db') IS NULL CREATE DATABASE [$db] COLLATE Arabic_CI_AS;"
sq -d "$db" -Q "IF OBJECT_ID(N'dbo.__Migrations') IS NULL CREATE TABLE dbo.__Migrations (Name NVARCHAR(200) NOT NULL PRIMARY KEY, Checksum CHAR(64) NOT NULL, AppliedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());"
applied=$(sq -d "$db" -h -1 -W -s '|' -Q "SET NOCOUNT ON; SELECT Name + '|' + Checksum FROM dbo.__Migrations")

ran=0
for f in "${files[@]}"; do
  [[ -f "$tmp/$f" ]] || { log "missing migration file $f in image"; exit 1; }
  sum=$(sha256sum "$tmp/$f" | cut -c1-64)
  prev=$(grep -E "^$f\|" <<<"$applied" | cut -d'|' -f2 || true)
  if [[ -n "$prev" ]]; then
    [[ "$prev" == "$sum" ]] || { log "WARNING: $f changed after it was applied (never edit an applied migration — add a new one)."; [[ "$e" == production ]] && exit 1; }
    continue
  fi
  if [[ "$mode" == --baseline ]]; then
    sq -d "$db" -Q "INSERT dbo.__Migrations (Name, Checksum) VALUES (N'$f', '$sum');"; log "baseline $f"; continue
  fi
  log "applying $f"
  # Old scripts say USE POSMAIN; point them at this environment's database.
  sed -E "s/\bPOSMAIN\b/$db/g" "$tmp/$f" | dc "$e" exec -T db bash -c 'cat > /tmp/migration.sql'
  sq -d "$db" -i /tmp/migration.sql -o /tmp/migration.log || { dc "$e" exec -T db tail -40 /tmp/migration.log; log "FAILED: $f"; exit 1; }
  sq -d "$db" -Q "INSERT dbo.__Migrations (Name, Checksum) VALUES (N'$f', '$sum');"
  ran=$((ran + 1))
done

# The app connects with its own login, never sa.
sq -d master -Q "IF SUSER_ID(N'$app_user') IS NULL CREATE LOGIN [$app_user] WITH PASSWORD = N'$app_pw', CHECK_POLICY = OFF; ELSE ALTER LOGIN [$app_user] WITH PASSWORD = N'$app_pw';"
sq -d "$db" -Q "IF USER_ID(N'$app_user') IS NULL CREATE USER [$app_user] FOR LOGIN [$app_user]; ALTER ROLE db_owner ADD MEMBER [$app_user];"
log "Migrations done: $ran new, database $db."
