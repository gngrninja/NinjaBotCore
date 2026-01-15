#!/bin/bash
# Test runner that loads .env.test file

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

# Load .env.test if it exists
if [ -f "$PROJECT_ROOT/.env.test" ]; then
    echo "Loading test environment from .env.test..."
    export $(grep -v '^#' "$PROJECT_ROOT/.env.test" | xargs)
else
    echo "Warning: .env.test not found. Copy .env.test.example to .env.test"
    exit 1
fi

# Ensure Docker PostgreSQL is running
if ! docker ps | grep -q ninjabot-test-db; then
    echo "Starting PostgreSQL test database..."
    cd "$PROJECT_ROOT"
    docker-compose -f docker-compose.dev.yml up -d postgres-dev

    echo "Waiting for PostgreSQL to be ready..."
    sleep 3

    # Apply migrations on first run
    echo "Applying migrations..."
    cd src
    dotnet ef database update --connection "$NINJABOT_ConnectionStrings__NinjaBot"
fi

# Run tests
cd "$PROJECT_ROOT/test/NinjaBotCore.Tests"

echo "Running tests with configuration:"
echo "  Provider: $NINJABOT_Database__Provider"
echo "  Connection: ${NINJABOT_ConnectionStrings__NinjaBot%%Password=*}Password=***"
echo ""

dotnet test "$@"
