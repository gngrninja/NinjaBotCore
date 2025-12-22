#!/bin/bash
set -e  # Exit on any error

# Configuration - uses environment variables with defaults
SERVICE_NAME="${NINJABOT_SERVICE_NAME}"
DEPLOY_DIR="${NINJABOT_DEPLOY_DIR}"
DEPLOY_USER="${NINJABOT_DEPLOY_USER}"

# Validate required config to avoid running with empty paths
if [ -z "$DEPLOY_DIR" ] || [ -z "$DEPLOY_USER" ]; then
  echo "Error: DEPLOY_DIR or DEPLOY_USER is empty. Check environment (/var/lib/jenkins/ninjabot.env)."
  exit 1
fi

# Normalize source path to the directory where this script lives (repo root)
SCRIPT_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
RSYNC_SRC="$SCRIPT_DIR/"

# Require passwordless sudo (or root) since Jenkins is non-interactive
SUDO="sudo -n"
if [ "$(id -u)" -eq 0 ]; then
  SUDO=""
elif ! $SUDO -v >/dev/null 2>&1; then
  echo "Error: passwordless sudo is required for deploy. Grant ${USER} NOPASSWD for mkdir/rsync/chown/docker or run as root."
  exit 1
fi

# Run commands as deploy user
run_as_deploy() {
  if [ -z "$SUDO" ]; then
    su -s /bin/sh -c "$*" "$DEPLOY_USER"
  else
    $SUDO -u "$DEPLOY_USER" "$@"
  fi
}

echo "========================================="
echo "NinjaBot Docker Deployment"
echo "========================================="
echo "Running from: $SCRIPT_DIR"
echo "Deploying to: $DEPLOY_DIR"

# Sync code to deployment directory
echo "[1/5] Syncing code to $DEPLOY_DIR..."
$SUDO mkdir -p "$DEPLOY_DIR"
$SUDO rsync -av --delete \
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
  "$RSYNC_SRC" "$DEPLOY_DIR"/

# Set proper permissions
echo "[2/5] Setting permissions..."
$SUDO chown -R "$DEPLOY_USER":"$DEPLOY_USER" "$DEPLOY_DIR"

TARGET_DIR="$DEPLOY_DIR"
# Ensure logs directory exists (as deploy user) but do not sync/overwrite
run_as_deploy sh -c "cd \"$TARGET_DIR\" && mkdir -p logs"

# Stop current containers
echo "[3/5] Stopping containers..."
run_as_deploy sh -c "cd \"$TARGET_DIR\" && /usr/bin/docker compose down" || echo "No containers running"

# Run database migrations
echo "[3.5/5] Running database migrations..."
run_as_deploy sh -c "cd \"$TARGET_DIR\" && dotnet ef database update --project src/NinjaBotCore.csproj" || {
  echo "⚠️  Warning: Migration failed. Container may still be starting or EF tools not available."
  echo "You can manually run: dotnet ef database update --project src/NinjaBotCore.csproj"
}

# Build and start new containers
echo "[4/5] Building and starting containers..."
run_as_deploy sh -c "cd \"$TARGET_DIR\" && /usr/bin/docker compose up -d --build"

# Check status
echo ""
echo "========================================="
echo "Deployment Status"
echo "========================================="
echo "[5/5] Checking container status..."
run_as_deploy sh -c "cd \"$TARGET_DIR\" && /usr/bin/docker compose ps"

# Verify container is running
if run_as_deploy sh -c "cd \"$TARGET_DIR\" && /usr/bin/docker compose ps" | grep -q "Up"; then
  echo ""
  echo "✅ Deployment successful! NinjaBot is running in Docker."
  echo ""
  echo "View logs: docker compose logs -f"
  exit 0
else
  echo ""
  echo "❌ Warning: Container may not be running properly."
  echo "Check logs: cd $DEPLOY_DIR && docker compose logs"
  exit 1
fi
