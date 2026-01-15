#!/bin/bash
# Test runner with multiple database options

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

# Load connection string from .env.development
if [ -f "$PROJECT_ROOT/.env.development" ]; then
    DB_CONNECTION=$(grep '^NINJABOT_ConnectionStrings__NinjaBot=' "$PROJECT_ROOT/.env.development" | cut -d'=' -f2-)
fi
DB_CONNECTION="${DB_CONNECTION:-Host=localhost;Port=5433;Database=ninjabot_test;Username=ninjabot_test;Password=test_password_local_only}"

show_help() {
    cat << EOF
NinjaBot Test Runner

Usage: ./test/run-tests.sh [OPTIONS]

Options:
    --sqlite            Run tests with SQLite (fastest, default)
    --local-postgres    Run tests with local Docker PostgreSQL
    --remote-postgres   Run tests with remote test database
    --all               Run tests with all database types
    --setup-docker      Set up local Docker PostgreSQL
    --teardown-docker   Tear down local Docker PostgreSQL
    -h, --help          Show this help message

Examples:
    ./test/run-tests.sh                      # Quick SQLite tests
    ./test/run-tests.sh --local-postgres     # Full PostgreSQL tests (Docker)
    ./test/run-tests.sh --all                # Run with all database types
    ./test/run-tests.sh --setup-docker       # Start Docker PostgreSQL
EOF
}

setup_docker() {
    echo "Setting up local PostgreSQL test database..."
    cd "$PROJECT_ROOT"
    docker-compose -f docker-compose.dev.yml up -d postgres-dev

    echo "Waiting for PostgreSQL to be ready..."
    sleep 3

    echo "Applying migrations..."
    cd src
    dotnet ef database update --connection "$DB_CONNECTION"

    echo "✅ Local PostgreSQL test database ready!"
}

teardown_docker() {
    echo "Tearing down local PostgreSQL test database..."
    cd "$PROJECT_ROOT"
    docker-compose -f docker-compose.dev.yml down -v
    echo "✅ Cleaned up!"
}

run_sqlite_tests() {
    echo ""
    echo "=========================================="
    echo "Running tests with SQLite"
    echo "=========================================="

    cd "$PROJECT_ROOT/test/NinjaBotCore.Tests"

    export NINJABOT_Database__Provider="Sqlite"
    unset NINJABOT_ConnectionStrings__NinjaBot

    dotnet test --logger "console;verbosity=minimal"

    echo "✅ SQLite tests completed"
}

run_local_postgres_tests() {
    echo ""
    echo "=========================================="
    echo "Running tests with Local Docker PostgreSQL"
    echo "=========================================="

    # Check if Docker is running
    if ! docker info > /dev/null 2>&1; then
        echo "❌ Docker is not running. Please start Docker first."
        exit 1
    fi

    # Check if container exists
    if ! docker ps | grep -q ninjabot-test-db; then
        echo "Local PostgreSQL not running. Starting it..."
        setup_docker
    fi

    cd "$PROJECT_ROOT/test/NinjaBotCore.Tests"

    export NINJABOT_Database__Provider="Postgres"
    export NINJABOT_ConnectionStrings__NinjaBot="$DB_CONNECTION"

    dotnet test --logger "console;verbosity=minimal"

    echo "✅ Local PostgreSQL tests completed"
}

run_remote_postgres_tests() {
    echo ""
    echo "=========================================="
    echo "Running tests with Remote PostgreSQL"
    echo "=========================================="

    if [ -z "$NINJABOT_REMOTE_TEST_CONNECTION" ]; then
        echo "❌ NINJABOT_REMOTE_TEST_CONNECTION not set"
        echo "Set it with: export NINJABOT_REMOTE_TEST_CONNECTION='Host=...'"
        exit 1
    fi

    cd "$PROJECT_ROOT/test/NinjaBotCore.Tests"

    export NINJABOT_Database__Provider="Postgres"
    export NINJABOT_ConnectionStrings__NinjaBot="$NINJABOT_REMOTE_TEST_CONNECTION"

    dotnet test --logger "console;verbosity=minimal"

    echo "✅ Remote PostgreSQL tests completed"
}

# Parse arguments
MODE="sqlite"

while [[ $# -gt 0 ]]; do
    case $1 in
        --sqlite)
            MODE="sqlite"
            shift
            ;;
        --local-postgres)
            MODE="local-postgres"
            shift
            ;;
        --remote-postgres)
            MODE="remote-postgres"
            shift
            ;;
        --all)
            MODE="all"
            shift
            ;;
        --setup-docker)
            setup_docker
            exit 0
            ;;
        --teardown-docker)
            teardown_docker
            exit 0
            ;;
        -h|--help)
            show_help
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            show_help
            exit 1
            ;;
    esac
done

# Run tests based on mode
case $MODE in
    sqlite)
        run_sqlite_tests
        ;;
    local-postgres)
        run_local_postgres_tests
        ;;
    remote-postgres)
        run_remote_postgres_tests
        ;;
    all)
        run_sqlite_tests
        run_local_postgres_tests
        if [ -n "$NINJABOT_REMOTE_TEST_CONNECTION" ]; then
            run_remote_postgres_tests
        else
            echo ""
            echo "ℹ️  Skipping remote PostgreSQL tests (NINJABOT_REMOTE_TEST_CONNECTION not set)"
        fi
        ;;
esac

echo ""
echo "✅ All tests completed successfully!"
