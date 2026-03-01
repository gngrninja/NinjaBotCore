#!/bin/bash
set -e

# Configuration from environment (set via /var/lib/jenkins/ninjabot.env)
DEPLOY_DIR="${NINJABOT_DEPLOY_DIR}"
DEPLOY_USER="${NINJABOT_DEPLOY_USER}"
DEPLOY_HOST="${NINJABOT_DEPLOY_HOST}"

if [ -z "$DEPLOY_DIR" ] || [ -z "$DEPLOY_USER" ]; then
  echo "Error: DEPLOY_DIR or DEPLOY_USER is empty. Check /var/lib/jenkins/ninjabot.env"
  exit 1
fi

if [ -z "$DEPLOY_HOST" ]; then
  echo "Error: DEPLOY_HOST is empty. Check /var/lib/jenkins/ninjabot.env"
  exit 1
fi

SCRIPT_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
RSYNC_SRC="$SCRIPT_DIR/"
SSH_TARGET="${DEPLOY_USER}@${DEPLOY_HOST}"

# Run a command on the remote host as the deploy user
run_remote() {
  ssh -o StrictHostKeyChecking=accept-new "$SSH_TARGET" "$@"
}

echo "========================================="
echo "NinjaBot Docker Deployment"
echo "========================================="
echo "Deploying to: $SSH_TARGET:$DEPLOY_DIR"

# Sync code
echo "[1/5] Syncing code..."
run_remote "mkdir -p \"$DEPLOY_DIR\" \"$DEPLOY_DIR/logs\" \"$DEPLOY_DIR/logs/helpers\""
rsync -av --delete \
  --exclude='.git' \
  --exclude='TestResults' \
  --exclude='*.user' \
  --exclude='bin' \
  --exclude='obj' \
  --exclude='config' \
  --exclude='logs' \
  --exclude='.nuget' \
  --exclude='.env*' \
  --exclude='config.json' \
  -e "ssh -o StrictHostKeyChecking=accept-new" \
  "$RSYNC_SRC" "$SSH_TARGET:$DEPLOY_DIR"/

# Run database migrations (source .env.production for connection string)
echo "[2/5] Running database migrations..."
run_remote "cd \"$DEPLOY_DIR\" && set -a && . .env.production && set +a && dotnet ef database update --project src/NinjaBotCore.csproj" || {
  echo "Warning: Migration failed. Check dotnet-ef tools on $DEPLOY_HOST"
  echo "You may need to apply migrations manually."
  exit 1
}

# Stop current containers
echo "[3/5] Stopping containers..."
run_remote "cd \"$DEPLOY_DIR\" && docker compose down" || echo "No containers running"

# Build and start new containers
echo "[4/5] Building and starting containers..."
run_remote "cd \"$DEPLOY_DIR\" && docker compose up -d --build"

# Verify
echo "[5/5] Checking container status..."
run_remote "cd \"$DEPLOY_DIR\" && docker compose ps"

if run_remote "cd \"$DEPLOY_DIR\" && docker compose ps" | grep -q "Up"; then
  echo ""
  echo "Deployment successful! NinjaBot is running."
  exit 0
else
  echo ""
  echo "Warning: Container may not be running properly."
  echo "Check logs: ssh $SSH_TARGET 'cd $DEPLOY_DIR && docker compose logs'"
  exit 1
fi
