-- Verify data migration from SQLite to PostgreSQL
-- Run this after migration to check row counts

SELECT 'AchCategories' as table_name, COUNT(*) as row_count FROM "AchCategories"
UNION ALL SELECT 'AwaySystem', COUNT(*) FROM "AwaySystem"
UNION ALL SELECT 'ChannelOutputs', COUNT(*) FROM "ChannelOutputs"
UNION ALL SELECT 'CurrentRaidTier', COUNT(*) FROM "CurrentRaidTier"
UNION ALL SELECT 'DiscordServers', COUNT(*) FROM "DiscordServers"
UNION ALL SELECT 'LogMonitoring', COUNT(*) FROM "LogMonitoring"
UNION ALL SELECT 'TriviaQuestion', COUNT(*) FROM "TriviaQuestion"
UNION ALL SELECT 'TriviaQuestionChoices', COUNT(*) FROM "TriviaQuestionChoices"
UNION ALL SELECT 'WowGuildAssociations', COUNT(*) FROM "WowGuildAssociations"
UNION ALL SELECT 'WowClassicGuild', COUNT(*) FROM "WowClassicGuild"
UNION ALL SELECT 'WowVanillaGuild', COUNT(*) FROM "WowVanillaGuild"
UNION ALL SELECT 'WclPosted', COUNT(*) FROM "WclPosted"
UNION ALL SELECT 'ServerSettings', COUNT(*) FROM "ServerSettings"
ORDER BY table_name;
