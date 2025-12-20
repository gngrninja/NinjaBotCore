#!/bin/bash
set -e  # Exit on any error

# Configuration - uses environment variables with defaults
SERVICE_NAME="${NINJABOT_SERVICE_NAME}"
DEPLOY_DIR="${NINJABOT_DEPLOY_DIR}"
DEPLOY_USER="${NINJABOT_DEPLOY_USER}"

echo "========================================="
echo "NinjaBot Docker Deployment"
echo "========================================="

# Sync code to deployment directory
echo "[1/5] Syncing code to $DEPLOY_DIR..."
sudo mkdir -p $DEPLOY_DIR
sudo rsync -av --delete \
  --exclude='.git' \
  --exclude='TestResults' \
  --exclude='*.user' \
  --exclude='bin' \
  --exclude='obj' \
  --exclude='config' \
  --exclude='.env*' \
  ./ $DEPLOY_DIR/

# Set proper permissions
echo "[2/5] Setting permissions..."
sudo chown -R $DEPLOY_USER:$DEPLOY_USER $DEPLOY_DIR

# Navigate to deployment directory
cd $DEPLOY_DIR

# Stop current containers
echo "[3/5] Stopping containers..."
sudo -u $DEPLOY_USER docker compose down || echo "No containers running"

# Build and start new containers
echo "[4/5] Building and starting containers..."
sudo -u $DEPLOY_USER docker compose up -d --build

# Wait for container to start
sleep 5

# Check status
echo ""
echo "========================================="
echo "Deployment Status"
echo "========================================="
echo "[5/5] Checking container status..."
sudo -u $DEPLOY_USER docker compose ps

# Verify container is running
if sudo -u $DEPLOY_USER docker compose ps | grep -q "Up"; then
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
