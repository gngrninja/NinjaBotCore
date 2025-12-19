#!/bin/bash
# Simple test runner - always uses Docker PostgreSQL

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

# Ensure Docker PostgreSQL is running
if ! docker ps | grep -q ninjabot-test-db; then
    echo "Starting PostgreSQL test database..."
    cd "$PROJECT_ROOT"
    docker-compose -f docker-compose.test.yml up -d

    echo "Waiting for PostgreSQL to be ready..."
    sleep 3

    # Apply migrations on first run
    if ! docker exec ninjabot-test-db psql -U ninjabot_test -d ninjabot_test -c '\dt' | grep -q __EFMigrationsHistory; then
        echo "Applying migrations..."
        cd src
        dotnet ef database update --connection "Host=localhost;Port=5433;Database=ninjabot_test;Username=ninjabot_test;Password=test_password_local_only"
    fi
fi

# Run tests
cd "$PROJECT_ROOT/test/NinjaBotCore.Tests"

export NINJABOT_Database__Provider="Postgres"
export NINJABOT_ConnectionStrings__NinjaBot="Host=localhost;Port=5433;Database=ninjabot_test;Username=ninjabot_test;Password=test_password_local_only"

dotnet test "$@"
