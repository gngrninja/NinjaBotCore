-- Check which migrations have been applied
SELECT
    "MigrationId",
    "ProductVersion"
FROM
    "__EFMigrationsHistory"
ORDER BY
    "MigrationId" DESC
LIMIT 5;
