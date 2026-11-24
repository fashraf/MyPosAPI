#!/usr/bin/env bash
# Shared helpers for the MyPOS deploy scripts. Every script takes the environment name first: staging | production.
set -euo pipefail
ROOT="${MYPOS_ROOT:-/opt/mypos}"

need_env() {
  [[ "${1:-}" =~ ^(staging|production)$ ]] || { echo "usage: $(basename "$0") staging|production ..." >&2; exit 2; }
  [[ -f "$ROOT/$1/.env" ]] || { echo "No $ROOT/$1/.env — run setup for this environment first." >&2; exit 2; }
}
dc()      { local e=$1; shift; docker compose --project-directory "$ROOT/$e" -f "$ROOT/$e/compose.yml" --env-file "$ROOT/$e/.env" "$@"; }
getenv()  { grep -E "^$2=" "$ROOT/$1/.env" | tail -1 | cut -d= -f2-; }
setenv()  { local f="$ROOT/$1/.env"; if grep -qE "^$2=" "$f"; then sed -i "s|^$2=.*|$2=$3|" "$f"; else echo "$2=$3" >> "$f"; fi; }
log()     { echo "[$(date -u +%H:%M:%S)] $*"; }

# Healthy = the site, the API and the database all answer through the public port.
# (Before the first web deploy there is no public port yet; then only the containers' state is checked.)
health() {
  local e=$1 wait=${2:-120} port end ok
  port=$(getenv "$e" WEB_PORT); end=$((SECONDS + wait))
  while (( SECONDS < end )); do
    ok=1
    if [[ "$(getenv "$e" WEB_TAG)" != none ]]; then
      curl -fsS -m 5 "http://127.0.0.1:$port/" >/dev/null 2>&1 || ok=0
      if [[ "$(getenv "$e" API_TAG)" != none ]]; then
        curl -fsS -m 5 "http://127.0.0.1:$port/health" >/dev/null 2>&1 || ok=0
        curl -fsS -m 5 "http://127.0.0.1:$port/health/db" >/dev/null 2>&1 || ok=0
      fi
    elif [[ "$(getenv "$e" API_TAG)" != none ]]; then
      [[ "$(dc "$e" ps --status running -q api 2>/dev/null)" ]] || ok=0
    fi
    (( ok )) && return 0
    sleep 3
  done
  return 1
}

record()  { echo "$(date -u +%FT%TZ) web=$(getenv "$1" WEB_TAG) api=$(getenv "$1" API_TAG) by=${2:-manual}" >> "$ROOT/$1/releases.log"; }

# Keep the last KEEP_RELEASES images of each app so rollback never has to download anything.
prune_images() {
  local e=$1 keep repo
  keep=$(getenv "$e" KEEP_RELEASES); keep=${keep:-5}
  for repo in "$(getenv "$e" WEB_IMAGE)" "$(getenv "$e" API_IMAGE)"; do
    docker image ls "$repo" --format '{{.CreatedAt}}\t{{.Repository}}:{{.Tag}}' | sort -r | tail -n +$((keep * 2 + 1)) | cut -f2 \
      | xargs -r docker image rm >/dev/null 2>&1 || true
  done
}
