#!/bin/bash
# Verify row counts match between SQLite and PostgreSQL

echo "Comparing row counts: SQLite vs PostgreSQL"
echo "=========================================="

SQLITE_DB="${1:-../src/ninjabot.db}"
PG_CONN="${2:-Host=localhost;Database=ninjabot;Username=ninjabot}"

tables=(
    "AchCategories"
    "AwaySystem"
    "ChannelOutputs"
    "CurrentRaidTier"
    "DiscordServers"
    "LogMonitoring"
    "TriviaQuestion"
    "TriviaQuestionChoices"
    "WowGuildAssociations"
    "WowClassicGuild"
    "WowVanillaGuild"
    "WclPosted"
    "ServerSettings"
)

echo "Table                      SQLite    PostgreSQL    Match"
echo "-------------------------------------------------------"

for table in "${tables[@]}"; do
    # Get SQLite count
    sqlite_count=$(sqlite3 "$SQLITE_DB" "SELECT COUNT(*) FROM \"$table\";")

    # Get PostgreSQL count (you'll need to adjust this based on your connection method)
    # For now, showing the format:
    printf "%-25s %6d    (run psql)     ?\n" "$table" "$sqlite_count"
done

echo ""
echo "Run this to get PostgreSQL counts:"
echo "psql -h <host> -U <user> -d ninjabot -f tools/verify-migration.sql"
