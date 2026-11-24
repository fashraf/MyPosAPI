#!/usr/bin/env bash
# rollback.sh <env>  — puts back the previous release in well under 2 minutes (images are already on the server).
# Run it again to go one more release back. The database is not touched (migrations only ever add).
source "$(dirname "$0")/lib.sh"
need_env "${1:-}"; e=$1
started=$SECONDS
log_file="$ROOT/$e/releases.log"
[[ -f "$log_file" && $(wc -l < "$log_file") -ge 2 ]] || { echo "Nothing to roll back to yet." >&2; exit 1; }
current=$(tail -1 "$log_file"); target=$(tail -2 "$log_file" | head -1)
t_web=$(sed -E 's/.* web=([^ ]+).*/\1/' <<<"$target"); t_api=$(sed -E 's/.* api=([^ ]+).*/\1/' <<<"$target")
log "Rolling $e back: $current  ->  web=$t_web api=$t_api"
setenv "$e" WEB_TAG "$t_web"; setenv "$e" API_TAG "$t_api"
s=(db); [[ "$t_api" != none ]] && s+=(api); [[ "$t_web" != none ]] && s+=(web)
dc "$e" up -d --remove-orphans "${s[@]}"
if health "$e" 90; then
  echo "$current rolled-back=$(date -u +%FT%TZ)" >> "$ROOT/$e/releases.bad.log"
  sed -i '$ d' "$log_file"
  log "OK: $e is back on web=$t_web api=$t_api in $((SECONDS - started))s."
else
  log "WARNING: rolled-back version is not healthy. Check: docker compose logs"; exit 1
fi
