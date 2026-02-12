#!/bin/bash
# Run bot in Docker development environment (matches production setup)
# This runs PostgreSQL, bot, and helpers in Docker containers
#
# Usage:
#   ./run-bot-dev.sh          # Start/restart the dev environment
#   ./run-bot-dev.sh --build  # Force rebuild the bot image
#   ./run-bot-dev.sh --logs   # Show live logs
#   ./run-bot-dev.sh --stop   # Stop the dev environment
#   ./run-bot-dev.sh --local  # Run bot locally (not in Docker) against Docker DB

set -e

# Ensure dotnet is in PATH for non-interactive shells (e.g., SSH from Jenkins)
if ! command -v dotnet &>/dev/null; then
  for dir in /usr/local/share/dotnet "$HOME/.dotnet"; do
    if [ -x "$dir/dotnet" ]; then
      export DOTNET_ROOT="$dir"
      export PATH="$PATH:$dir"
      break
    fi
  done
  [ -d "$HOME/.dotnet/tools" ] && export PATH="$PATH:$HOME/.dotnet/tools"
fi

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$PROJECT_ROOT"

# Parse arguments
BUILD_FLAG=""
SHOW_LOGS=false
STOP_ONLY=false
RUN_LOCAL=false

for arg in "$@"; do
    case $arg in
        --build)
            BUILD_FLAG="--build"
            ;;
        --logs)
            SHOW_LOGS=true
            ;;
        --stop)
            STOP_ONLY=true
            ;;
        --local)
            RUN_LOCAL=true
            ;;
    esac
done

# Check if .env.development exists
if [ ! -f "$PROJECT_ROOT/.env.development" ]; then
    echo "Error: .env.development not found!"
    echo "Copy .env.development.example to .env.development and add your Discord token"
    exit 1
fi

# Verify critical vars are set
if grep -q "NINJABOT_Token=your-discord-bot-token-here" .env.development 2>/dev/null; then
    echo "Error: Set your Discord token in .env.development"
    exit 1
fi

# Auto-create .env.development.docker if missing (needed for Docker networking)
if [ ! -f "$PROJECT_ROOT/.env.development.docker" ]; then
    echo "Creating .env.development.docker from example..."
    cp "$PROJECT_ROOT/.env.development.docker.example" "$PROJECT_ROOT/.env.development.docker"
fi

# Load environment variables for migrations (uses localhost connection from .env.development)
export $(grep -v '^#' .env.development | grep -v '^$' | xargs)
DB_CONNECTION="$NINJABOT_ConnectionStrings__NinjaBot"

# Extract username and database from connection string for pg_isready
DB_USER=$(echo "$DB_CONNECTION" | sed -n 's/.*Username=\([^;]*\).*/\1/p')
DB_NAME=$(echo "$DB_CONNECTION" | sed -n 's/.*Database=\([^;]*\).*/\1/p')

# Handle stop command
if [ "$STOP_ONLY" = true ]; then
    echo "Stopping dev environment..."
    docker compose -f docker-compose.dev.yml down
    echo "Dev environment stopped."
    exit 0
fi

# Handle local mode (bot runs outside Docker, DB + helpers in Docker)
if [ "$RUN_LOCAL" = true ]; then
    echo "Starting in LOCAL mode (bot outside Docker)..."

    # Start database and helpers
    if ! docker ps | grep -q ninjabot-test-db; then
        echo "Starting dev database..."
        docker compose -f docker-compose.dev.yml up -d postgres-dev
        echo "Waiting for database to be healthy..."
        sleep 5
    fi

    # Apply migrations
    echo "Applying migrations..."
    cd src
    dotnet ef database update --connection "$DB_CONNECTION"
    cd ..

    # Start helpers in Docker
    echo "Starting helpers container..."
    docker compose -f docker-compose.dev.yml up -d $BUILD_FLAG ninjabot-helpers-dev

    echo ""
    echo "=========================================="
    echo "Starting NinjaBot (Local Mode)"
    echo "=========================================="
    echo "Bot: Running locally via dotnet run"
    echo "Helpers: Docker container"
    echo "Database: Docker (localhost:5433)"
    echo "API: http://localhost:5100"
    echo "=========================================="
    echo "Helper logs: docker compose -f docker-compose.dev.yml logs -f ninjabot-helpers-dev"
    echo ""

    cd src
    dotnet run
    exit 0
fi

# Docker mode (default)
echo ""
echo "=========================================="
echo "Starting NinjaBot (Docker Mode)"
echo "=========================================="
echo "Bot: Docker container"
echo "Database: Docker container"
echo "API: http://localhost:5100"
echo "=========================================="
echo ""

# Stop existing containers
echo "Stopping existing containers..."
docker compose -f docker-compose.dev.yml down 2>/dev/null || true

# Step 1: Start ONLY the database first
echo "Starting database..."
docker compose -f docker-compose.dev.yml up -d postgres-dev

# Step 2: Wait for database to be healthy
echo "Waiting for database to be healthy..."
until docker compose -f docker-compose.dev.yml exec -T postgres-dev pg_isready -U "$DB_USER" -d "$DB_NAME" > /dev/null 2>&1; do
    echo "  Database not ready yet, waiting..."
    sleep 2
done
echo "Database is ready!"

# Step 3: Apply migrations BEFORE starting the bot
echo "Applying migrations..."
cd src
dotnet ef database update --connection "$DB_CONNECTION"
cd ..
echo "Migrations applied!"

# Step 4: Now start the bot and helpers containers
echo "Starting bot and helpers containers..."
docker compose -f docker-compose.dev.yml up -d $BUILD_FLAG ninjabot-dev ninjabot-helpers-dev

echo ""
echo "=========================================="
echo "Dev environment is running!"
echo "=========================================="
echo ""
echo "Services:"
echo "  - Bot:      Running in Docker"
echo "  - Helpers:  Running in Docker (realm watcher)"
echo "  - Database: PostgreSQL (localhost:5433)"
echo "  - API:      http://localhost:5100"
echo ""
echo "Commands:"
echo "  View logs:  docker compose -f docker-compose.dev.yml logs -f"
echo "  Bot logs:   docker compose -f docker-compose.dev.yml logs -f ninjabot-dev"
echo "  Helper logs: docker compose -f docker-compose.dev.yml logs -f ninjabot-helpers-dev"
echo "  Stop:       ./run-bot-dev.sh --stop"
echo "  Rebuild:    ./run-bot-dev.sh --build"
echo ""

# Show logs if requested
if [ "$SHOW_LOGS" = true ]; then
    echo "Showing logs (Ctrl+C to exit)..."
    docker compose -f docker-compose.dev.yml logs -f ninjabot-dev ninjabot-helpers-dev
fi
