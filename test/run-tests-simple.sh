#!/bin/bash
# Simple test runner - always uses Docker PostgreSQL

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

# Load connection string from .env.development
if [ -f "$PROJECT_ROOT/.env.development" ]; then
    DB_CONNECTION=$(grep '^NINJABOT_ConnectionStrings__NinjaBot=' "$PROJECT_ROOT/.env.development" | cut -d'=' -f2-)
    DB_USER=$(echo "$DB_CONNECTION" | sed -n 's/.*Username=\([^;]*\).*/\1/p')
    DB_NAME=$(echo "$DB_CONNECTION" | sed -n 's/.*Database=\([^;]*\).*/\1/p')
fi
DB_CONNECTION="${DB_CONNECTION:-Host=localhost;Port=5433;Database=ninjabot_test;Username=ninjabot_test;Password=test_password_local_only}"
DB_USER="${DB_USER:-ninjabot_test}"
DB_NAME="${DB_NAME:-ninjabot_test}"

# Ensure Docker PostgreSQL is running
if ! docker ps | grep -q ninjabot-test-db; then
    echo "Starting PostgreSQL test database..."
    cd "$PROJECT_ROOT"
    docker-compose -f docker-compose.dev.yml up -d postgres-dev

    echo "Waiting for PostgreSQL to be ready..."
    sleep 3

    # Apply migrations on first run
    if ! docker exec ninjabot-test-db psql -U "$DB_USER" -d "$DB_NAME" -c '\dt' | grep -q __EFMigrationsHistory; then
        echo "Applying migrations..."
        cd src
        dotnet ef database update --connection "$DB_CONNECTION"
    fi
fi

# Run tests
cd "$PROJECT_ROOT/test/NinjaBotCore.Tests"

export NINJABOT_Database__Provider="Postgres"
export NINJABOT_ConnectionStrings__NinjaBot="$DB_CONNECTION"

dotnet test "$@"
