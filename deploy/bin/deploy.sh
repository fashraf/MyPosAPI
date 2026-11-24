#!/usr/bin/env bash
# deploy.sh <env> [web=<tag>] [api=<tag>]
# Pulls the new image(s), backs up + migrates the database when the API changes, swaps containers,
# checks health, and puts the previous version back automatically if the new one is not healthy.
source "$(dirname "$0")/lib.sh"
need_env "${1:-}"; e=$1; shift
started=$SECONDS
prev_web=$(getenv "$e" WEB_TAG); prev_api=$(getenv "$e" API_TAG)
new_web=$prev_web; new_api=$prev_api
for kv in "$@"; do
  case "$kv" in
    web=*) new_web=${kv#web=} ;;
    api=*) new_api=${kv#api=} ;;
    *) echo "unknown argument: $kv" >&2; exit 2 ;;
  esac
done
log "Deploying $e: web $prev_web -> $new_web, api $prev_api -> $new_api"
setenv "$e" WEB_TAG "$new_web"; setenv "$e" API_TAG "$new_api"

services() { local s=(db); [[ "$(getenv "$e" API_TAG)" != none ]] && s+=(api); [[ "$(getenv "$e" WEB_TAG)" != none ]] && s+=(web); echo "${s[@]}"; }
restore() { setenv "$e" WEB_TAG "$prev_web"; setenv "$e" API_TAG "$prev_api"; # shellcheck disable=SC2046
  dc "$e" up -d $(services) || true; }

changed=()
[[ "$new_web" != "$prev_web" && "$new_web" != none ]] && changed+=(web)
[[ "$new_api" != "$prev_api" && "$new_api" != none ]] && changed+=(api)
if (( ${#changed[@]} )) && ! dc "$e" pull "${changed[@]}"; then log "Pull failed — nothing changed."; restore; exit 1; fi

if [[ "$new_api" != none && ( "$new_api" != "$prev_api" || ! -f "$ROOT/$e/releases.log" ) ]]; then
  "$(dirname "$0")/backup.sh" "$e" "pre-$new_api" || { log "Backup failed — deploy stopped, nothing changed."; restore; exit 1; }
  "$(dirname "$0")/migrate.sh" "$e" || { log "Migration failed — old version keeps running."; restore; exit 1; }
fi

# shellcheck disable=SC2046
dc "$e" up -d --remove-orphans $(services)
if health "$e" 150; then
  record "$e" "${DEPLOYED_BY:-manual}"
  prune_images "$e"
  log "OK: $e is healthy on web=$new_web api=$new_api ($((SECONDS - started))s)."
else
  log "New version is NOT healthy — rolling back to web=$prev_web api=$prev_api."
  dc "$e" logs --tail 80 api web || true
  restore
  health "$e" 90 && log "Rolled back, previous version healthy." || log "WARNING: previous version is not healthy either."
  exit 1
fi
