#!/bin/bash
# Run bot in development mode against Docker test database

set -e

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Ensure test database is running
if ! docker ps | grep -q ninjabot-test-db; then
    echo "Starting test database..."
    docker-compose -f docker-compose.test.yml up -d
    sleep 3

    echo "Applying migrations..."
    cd src
    dotnet ef database update --connection "Host=localhost;Port=5433;Database=ninjabot_test;Username=ninjabot_test;Password=test_password_local_only"
    cd ..
fi

# Check if .env.development exists
if [ ! -f "$PROJECT_ROOT/.env.development" ]; then
    echo "Error: .env.development not found!"
    echo "Copy .env.development and add your Discord token"
    exit 1
fi

# Load environment variables
echo "Loading development environment..."
export $(grep -v '^#' .env.development | xargs)

# Verify critical vars are set
if [ -z "$NINJABOT_Token" ] || [ "$NINJABOT_Token" = "your-discord-bot-token-here" ]; then
    echo "Error: Set your Discord token in .env.development"
    exit 1
fi

echo ""
echo "=========================================="
echo "Starting NinjaBot (Development Mode)"
echo "=========================================="
echo "Database: PostgreSQL (Docker test DB)"
echo "Host: localhost:5433"
echo "Database: ninjabot_test"
echo "=========================================="
echo ""

# Run the bot
cd src
dotnet run
