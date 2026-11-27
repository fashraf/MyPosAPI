#!/usr/bin/env bash
# setup.sh <env> <web-port> — run once per environment on a fresh Ubuntu server (as root). Safe to run again.
# Installs Docker, creates /opt/mypos/<env> with a .env full of fresh random secrets, and opens the firewall.
set -euo pipefail
e=${1:?staging|production}; port=${2:?web port, e.g. 80 for production, 8080 for staging}
ROOT=/opt/mypos
if ! command -v docker >/dev/null; then
  curl -fsSL https://get.docker.com | sh
fi
systemctl enable --now docker
mkdir -p "$ROOT/$e/backups" "$ROOT/bin"
chown 10001:0 "$ROOT/$e/backups"; chmod 775 "$ROOT/$e/backups"   # SQL Server runs as uid 10001 inside its container
rnd() { tr -dc 'A-Za-z0-9' </dev/urandom | head -c "$1"; }
if [[ ! -f "$ROOT/$e/.env" ]]; then
  ip=$(curl -fsS -4 https://ifconfig.me 2>/dev/null || hostname -I | awk '{print $1}')
  db=MyPosDB; [[ "$e" == staging ]] && db=MyPosDB_Staging
  aspnet=Production; [[ "$e" == staging ]] && aspnet=Staging
  url="http://$ip"; [[ "$port" != 80 ]] && url="$url:$port"
  cat > "$ROOT/$e/.env" <<ENV
ENV_NAME=$e
WEB_PORT=$port
PUBLIC_URL=$url
ASPNET_ENV=$aspnet
WEB_IMAGE=ghcr.io/fashraf/mypos-web
WEB_TAG=none
API_IMAGE=ghcr.io/fashraf/mypos-api
API_TAG=none
DB_NAME=$db
DB_SA_PASSWORD=Sa#$(rnd 24)
APP_DB_USER=mypos_app
APP_DB_PASSWORD=App#$(rnd 24)
JWT_SIGNING_KEY=$(rnd 64)
KEEP_RELEASES=5
ENV
  chmod 600 "$ROOT/$e/.env"
  echo "Created $ROOT/$e/.env with new random passwords."
fi
# Nightly backup at 02:30 server time (keeps the last 14).
echo "30 2 * * * root /opt/mypos/bin/backup.sh $e nightly >> /var/log/mypos-backup-$e.log 2>&1" > "/etc/cron.d/mypos-backup-$e"
chmod 644 "/etc/cron.d/mypos-backup-$e"
if command -v ufw >/dev/null; then ufw allow OpenSSH >/dev/null; ufw allow "$port/tcp" >/dev/null; ufw --force enable >/dev/null; fi
echo "Environment $e ready on port $port. The first deploy from GitHub will start it."
