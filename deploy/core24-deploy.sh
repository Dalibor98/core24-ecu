#!/bin/bash
# core24-deploy: put a new version of CORE24 on the server, safely.
#
# Run from the PC, inside the repository (only COMMITTED code is sent):
#   Git Bash:    git archive HEAD | ssh root@SERVER core24-deploy
#   PowerShell:  cmd /c "git archive HEAD | ssh root@SERVER core24-deploy"
#   (PowerShell 5.1's own pipe turns binary data into text and breaks the archive, hence cmd /c.)
#
# What it does:
#   1. saves the uploaded code in a NEW folder (the running site is not touched yet)
#   2. builds the new Docker image; if the build fails, it stops here and the old version keeps running
#   3. backs up the database
#   4. swaps the folders (/opt/core24 = new, /opt/core24.prev = old; .env is kept) and restarts the app
#   5. waits for /health; if the new version doesn't come up, it rolls back: old code, old image, old database
#
# Install on the server:  scp deploy/core24-deploy.sh root@SERVER:/usr/local/bin/core24-deploy
#                         ssh root@SERVER chmod +x /usr/local/bin/core24-deploy
set -euo pipefail

APP=/opt/core24              # what runs now
NEW=/opt/core24.new          # the version being deployed
PREV=/opt/core24.prev        # the version before (for rollback)
BACKUPS=/opt/core24-backups  # database copies, the newest 10 are kept
PROJECT="docker compose -p core24"   # same project name as the folder, so volumes stay the same

say()  { echo "==> $*"; }
fail() { echo "!!! $*" >&2; exit 1; }

# Only one deploy at a time.
exec 9>/var/lock/core24-deploy
flock -n 9 || fail "Another deploy is running."

[ -t 0 ] && fail "No code received. Usage: git archive HEAD | ssh root@SERVER core24-deploy"
[ -f "$APP/.env" ] || fail "$APP/.env not found."

DOMAIN=$(grep '^DOMAIN=' "$APP/.env" | cut -d= -f2- | tr -d '\r')
DATA=$(docker volume inspect -f '{{.Mountpoint}}' core24_app-data)
DB_OWNER=$(stat -c %u:%g "$DATA/core24.db")   # the app's user inside the container (1654); copies must keep it
TS=$(date +%Y%m%d-%H%M%S)

# ---- 1. Receive the code ----------------------------------------------------
say "1/5 Receiving the code"
TAR=$(mktemp /tmp/core24-XXXXXX.tar)
trap 'rm -f "$TAR"' EXIT
cat > "$TAR"
COMMIT=$(git get-tar-commit-id < "$TAR" 2>/dev/null | cut -c1-7 || true)
rm -rf "$NEW"; mkdir -p "$NEW"
tar -x -C "$NEW" -f "$TAR" || { rm -rf "$NEW"; fail "The archive is broken (from PowerShell? use cmd /c, see the top of this script)."; }
[ -f "$NEW/docker-compose.yml" ] && [ -f "$NEW/Dockerfile" ] || { rm -rf "$NEW"; fail "This is not the CORE24 repository."; }
cp -p "$APP/.env" "$NEW/.env"
echo "${COMMIT:-unknown} $TS" > "$NEW/.deployed"
echo "    commit ${COMMIT:-unknown} (now running: $(cut -d' ' -f1 "$APP/.deployed" 2>/dev/null || echo unknown))"

# ---- 2. Build (the old version keeps serving) -------------------------------
say "2/5 Building the new version (2-3 minutes; the site stays up)"
docker image inspect core24-app:latest >/dev/null 2>&1 && docker tag core24-app:latest core24-app:previous
if ! $PROJECT --project-directory "$NEW" -f "$NEW/docker-compose.yml" build app; then
  rm -rf "$NEW"
  fail "BUILD FAILED. Nothing changed: the site still runs the old version."
fi

# ---- 3. Back up the database ------------------------------------------------
say "3/5 Backing up the database"
mkdir -p "$BACKUPS"
BACKUP="$BACKUPS/core24-$TS.db"
if command -v sqlite3 >/dev/null; then
  sqlite3 "$DATA/core24.db" ".backup '$BACKUP'"     # consistent copy even while the app runs
else
  cp -p "$DATA/core24.db" "$BACKUP"
fi
chown "$DB_OWNER" "$BACKUP"
ls -1t "$BACKUPS"/core24-*.db | tail -n +11 | xargs -r rm -f
echo "    $BACKUP"

# ---- 4. Switch -------------------------------------------------------------
say "4/5 Switching to the new version (the site is down for a few seconds)"
rm -rf "$PREV"
mv "$APP" "$PREV"
mv "$NEW" "$APP"
cd "$APP"
$PROJECT up -d
# Caddy reads its Caddyfile only at start: restart it only when the file changed.
cmp -s "$APP/Caddyfile" "$PREV/Caddyfile" || $PROJECT restart caddy

# ---- 5. Check, roll back if needed -----------------------------------------
say "5/5 Waiting for the app to answer /health"
healthy() { curl -fsS -o /dev/null --max-time 5 --resolve "$DOMAIN:443:127.0.0.1" "https://$DOMAIN/health"; }
for _ in $(seq 1 45); do
  if healthy; then
    docker image prune -f >/dev/null   # remove old, unused image layers
    say "DONE. https://$DOMAIN runs commit ${COMMIT:-unknown}."
    exit 0
  fi
  sleep 2
done

echo "!!! The new version did not start. Last log lines:" >&2
$PROJECT logs --tail 40 app >&2 || true
say "Rolling back to the previous version"
$PROJECT stop app
cp "$BACKUP" "$DATA/core24.db"        # undo any migration the new version ran
chown "$DB_OWNER" "$DATA/core24.db"   # owned by root, the app could only read it (found in the first rollback test)
rm -f "$DATA/core24.db-wal" "$DATA/core24.db-shm"
rm -rf "$APP.failed"; mv "$APP" "$APP.failed"; mv "$PREV" "$APP"
docker tag core24-app:previous core24-app:latest
cd "$APP"
$PROJECT up -d
cmp -s "$APP/Caddyfile" "$APP.failed/Caddyfile" || $PROJECT restart caddy
for _ in $(seq 1 30); do healthy && fail "ROLLED BACK. The old version runs again. The failed code is in $APP.failed."; sleep 2; done
fail "ROLLBACK DID NOT COME UP EITHER. Check: cd $APP && docker compose logs app"
