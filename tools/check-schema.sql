-- Check CurrentRaidTier column types in PostgreSQL
SELECT
    column_name,
    data_type,
    character_maximum_length,
    numeric_precision,
    is_nullable
FROM
    information_schema.columns
WHERE
    table_name = 'CurrentRaidTier'
ORDER BY
    ordinal_position;
