#!/bin/bash

# PostgreSQL Compatibility Checker for NinjaBotCore
# This script scans the codebase for potential PostgreSQL compatibility issues

set -e

echo "========================================"
echo "PostgreSQL Compatibility Analysis"
echo "========================================"
echo ""

ISSUES_FOUND=0

# Color codes
RED='\033[0;31m'
YELLOW='\033[1;33m'
GREEN='\033[0;32m'
NC='\033[0m' # No Color

# Function to report issues
report_issue() {
    local severity=$1
    local message=$2
    local file=$3
    local line=$4

    ISSUES_FOUND=$((ISSUES_FOUND + 1))

    if [ "$severity" = "ERROR" ]; then
        echo -e "${RED}[ERROR]${NC} $message"
    else
        echo -e "${YELLOW}[WARNING]${NC} $message"
    fi

    if [ -n "$file" ]; then
        echo "  File: $file"
        if [ -n "$line" ]; then
            echo "  Line: $line"
        fi
    fi
    echo ""
}

# Navigate to project root
cd "$(dirname "$0")/.."

echo "1. Checking for SQLite-specific code..."
echo "----------------------------------------"

# Check for AUTOINCREMENT (SQLite) vs SERIAL/IDENTITY (PostgreSQL)
if grep -r "AUTOINCREMENT" --include="*.cs" src/ 2>/dev/null; then
    report_issue "WARNING" "Found AUTOINCREMENT keyword (SQLite-specific)" "" ""
fi

# Check for SQLite connection code
if grep -r "SqliteConnection\|UseSqlite" --include="*.cs" src/ --exclude-dir=Database 2>/dev/null; then
    echo -e "${GREEN}[INFO]${NC} Found SQLite references (expected for backward compatibility)"
    echo ""
fi

echo "2. Checking for case-sensitive string comparisons..."
echo "-----------------------------------------------------"

# Check for .Contains() without case handling
# This is a warning because PostgreSQL is case-sensitive by default
CONTAINS_COUNT=$(grep -r "\.Contains(" --include="*.cs" src/ | grep -v "ToLower\|ToUpper\|StringComparison" | wc -l || true)
if [ "$CONTAINS_COUNT" -gt 0 ]; then
    echo -e "${YELLOW}[INFO]${NC} Found $CONTAINS_COUNT string Contains() calls without explicit case handling"
    echo "  PostgreSQL is case-sensitive. Consider using .ToLower() or StringComparison options"
    echo ""
fi

echo "3. Checking for DateTime string conversions..."
echo "-----------------------------------------------"

# Check for DateTime.ToString() without format specifier
if grep -r "DateTime\.ToString()" --include="*.cs" src/ 2>/dev/null; then
    report_issue "WARNING" "Found DateTime.ToString() without format specifier" "" ""
    echo "  PostgreSQL timestamp format may differ from SQLite"
fi

# Check for DateTime parsing without culture
if grep -r "DateTime\.Parse\|DateTime\.ParseExact" --include="*.cs" src/ | grep -v "CultureInfo" 2>/dev/null; then
    echo -e "${YELLOW}[INFO]${NC} Found DateTime parsing without explicit culture"
    echo "  Consider using InvariantCulture for database operations"
    echo ""
fi

echo "4. Checking for SQL injection vulnerabilities..."
echo "-------------------------------------------------"

# Check for string concatenation in SQL queries
if grep -r "FromSqlRaw.*+\|ExecuteSqlRaw.*+" --include="*.cs" src/ 2>/dev/null; then
    report_issue "ERROR" "Potential SQL injection: String concatenation in raw SQL" "" ""
    echo "  Use parameterized queries instead"
fi

# Check for interpolated strings in SQL
if grep -r 'FromSqlRaw.*\$"\|ExecuteSqlRaw.*\$"' --include="*.cs" src/ 2>/dev/null; then
    echo -e "${YELLOW}[WARNING]${NC} Found interpolated strings in SQL methods"
    echo "  Ensure FromSqlInterpolated/ExecuteSqlInterpolated is used for safety"
    echo ""
fi

echo "5. Checking entity model ID types..."
echo "-------------------------------------"

# Check for int IDs (should be long after migration)
INT_ID_COUNT=$(grep -r "public int.*Id { get; set; }" --include="*.cs" src/Database/ 2>/dev/null | wc -l || true)
if [ "$INT_ID_COUNT" -gt 0 ]; then
    report_issue "ERROR" "Found $INT_ID_COUNT entity properties using 'int' for ID fields" "" ""
    echo "  PostgreSQL migration changed all IDs to bigint (long in C#)"
    grep -r "public int.*Id { get; set; }" --include="*.cs" src/Database/ 2>/dev/null || true
    echo ""
fi

echo "6. Checking for LIKE queries (case sensitivity)..."
echo "---------------------------------------------------"

# Check for .Contains/.StartsWith/.EndsWith in LINQ (generates LIKE in SQL)
LIKE_COUNT=$(grep -r "\.Where.*\.Contains\|\.Where.*\.StartsWith\|\.Where.*\.EndsWith" --include="*.cs" src/ | grep -v "ToLower\|ToUpper" | wc -l || true)
if [ "$LIKE_COUNT" -gt 0 ]; then
    echo -e "${YELLOW}[INFO]${NC} Found $LIKE_COUNT LINQ queries with string comparisons"
    echo "  PostgreSQL LIKE is case-sensitive. SQLite LIKE is case-insensitive."
    echo "  Use .ToLower() or EF.Functions.ILike() for case-insensitive searches"
    echo ""
fi

echo "7. Checking for boolean integer representations..."
echo "---------------------------------------------------"

# Check for boolean fields stored as int
BOOL_INT_COUNT=$(grep -r "public int.*Is[A-Z]\|public int.*Status" --include="*.cs" src/Database/ 2>/dev/null | wc -l || true)
if [ "$BOOL_INT_COUNT" -gt 0 ]; then
    report_issue "WARNING" "Found $BOOL_INT_COUNT properties that might be boolean but use int" "" ""
    echo "  PostgreSQL uses native boolean type, not 0/1 integers"
    grep -r "public int.*Is[A-Z]\|public int.*Status" --include="*.cs" src/Database/ 2>/dev/null || true
    echo ""
fi

echo "8. Checking migration files..."
echo "-------------------------------"

# Check if psqlredux migration exists
if [ -f "src/Migrations/20251218011942_psqlredux.cs" ]; then
    echo -e "${GREEN}[OK]${NC} PostgreSQL migration file found"

    # Check for raw SQL in migration
    if grep -q "migrationBuilder.Sql" src/Migrations/20251218011942_psqlredux.cs; then
        echo -e "${YELLOW}[INFO]${NC} Migration contains raw SQL statements"
        echo "  Verify these are PostgreSQL-compatible"
    fi
    echo ""
else
    report_issue "ERROR" "PostgreSQL migration file not found" "" ""
fi

echo "9. Checking for proper NULL handling..."
echo "----------------------------------------"

# Check for Nullable<T> vs T? syntax inconsistency
NULLABLE_T=$(grep -r "Nullable<" --include="*.cs" src/Database/ | wc -l || true)
T_NULLABLE=$(grep -r "public.*\?" --include="*.cs" src/Database/ | grep -v "Nullable<" | wc -l || true)

if [ "$NULLABLE_T" -gt 0 ] && [ "$T_NULLABLE" -gt 0 ]; then
    echo -e "${YELLOW}[INFO]${NC} Mixed nullable syntax found (Nullable<T> and T?)"
    echo "  Nullable<T>: $NULLABLE_T occurrences"
    echo "  T?: $T_NULLABLE occurrences"
    echo "  Consider standardizing on T? syntax"
    echo ""
fi

echo "10. Checking database configuration..."
echo "---------------------------------------"

# Check if DatabaseConfigurator exists
if [ -f "src/Database/DatabaseConfigurator.cs" ]; then
    echo -e "${GREEN}[OK]${NC} DatabaseConfigurator.cs found"

    # Verify it supports PostgreSQL
    if grep -q "UseNpgsql" src/Database/DatabaseConfigurator.cs; then
        echo -e "${GREEN}[OK]${NC} PostgreSQL support configured"
    else
        report_issue "ERROR" "DatabaseConfigurator missing UseNpgsql configuration" "" ""
    fi
    echo ""
else
    report_issue "ERROR" "DatabaseConfigurator.cs not found" "" ""
fi

# Check for config.json.example
if [ -f "config/config.json.example" ] || [ -f "config.json.example" ]; then
    echo -e "${GREEN}[OK]${NC} Configuration example file found"
else
    echo -e "${YELLOW}[WARNING]${NC} No config.json.example file found"
    echo "  Consider creating an example configuration file"
    echo ""
fi

echo "11. Checking for transaction isolation issues..."
echo "-------------------------------------------------"

# Check for SaveChanges without transaction
SAVECHANGES_COUNT=$(grep -r "SaveChanges\|SaveChangesAsync" --include="*.cs" src/ | wc -l || true)
TRANSACTION_COUNT=$(grep -r "BeginTransaction\|TransactionScope" --include="*.cs" src/ | wc -l || true)

if [ "$SAVECHANGES_COUNT" -gt "$TRANSACTION_COUNT" ]; then
    echo -e "${YELLOW}[INFO]${NC} Found $SAVECHANGES_COUNT SaveChanges calls, $TRANSACTION_COUNT explicit transactions"
    echo "  PostgreSQL has stricter transaction semantics than SQLite"
    echo "  Consider using explicit transactions for critical operations"
    echo ""
fi

echo "12. Checking for sequence/identity configuration..."
echo "----------------------------------------------------"

# Check migration for identity configuration
if grep -q "NpgsqlValueGenerationStrategy" src/Migrations/*_psqlredux.cs 2>/dev/null; then
    echo -e "${GREEN}[OK]${NC} PostgreSQL identity generation strategy configured"
else
    echo -e "${YELLOW}[WARNING]${NC} No PostgreSQL identity generation strategy found in migrations"
    echo "  Verify auto-increment columns are properly configured"
fi
echo ""

echo "========================================"
echo "Summary"
echo "========================================"

if [ "$ISSUES_FOUND" -eq 0 ]; then
    echo -e "${GREEN}✓ No critical issues found${NC}"
else
    echo -e "${YELLOW}! Found $ISSUES_FOUND potential issues${NC}"
    echo ""
    echo "Recommendations:"
    echo "1. Run the unit and integration tests"
    echo "2. Test all CRUD operations against PostgreSQL"
    echo "3. Verify case-sensitive string comparisons work as expected"
    echo "4. Check DateTime handling across different timezones"
    echo "5. Validate all foreign key relationships"
fi

echo ""
echo "To run tests:"
echo "  cd test/NinjaBotCore.Tests"
echo "  dotnet test"
echo ""

exit 0
