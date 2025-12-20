#!/bin/bash
set -e  # Exit on any error

# Configuration - uses environment variables with defaults
SERVICE_NAME="${NINJABOT_SERVICE_NAME}"
DEPLOY_DIR="${NINJABOT_DEPLOY_DIR}"
DEPLOY_USER="${NINJABOT_DEPLOY_USER}"

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
  --exclude='.env*' \
  ./ "$DEPLOY_DIR"/

# Set proper permissions
echo "[2/5] Setting permissions..."
$SUDO chown -R "$DEPLOY_USER":"$DEPLOY_USER" "$DEPLOY_DIR"

# Navigate to deployment directory
cd "$DEPLOY_DIR"

# Stop current containers
echo "[3/5] Stopping containers..."
run_as_deploy /usr/bin/docker compose down || echo "No containers running"

# Build and start new containers
echo "[4/5] Building and starting containers..."
run_as_deploy /usr/bin/docker compose up -d --build

# Wait for container to start
sleep 5

# Check status
echo ""
echo "========================================="
echo "Deployment Status"
echo "========================================="
echo "[5/5] Checking container status..."
run_as_deploy /usr/bin/docker compose ps

# Verify container is running
if run_as_deploy /usr/bin/docker compose ps | grep -q "Up"; then
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
