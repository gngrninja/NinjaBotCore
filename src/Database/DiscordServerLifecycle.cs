using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace NinjaBotCore.Database;

/// <summary>
/// Serialize PostgreSQL transitions with Helpers retention using a transaction-scoped server-ID lock.
/// SQLite uses its normal write transaction. Use a fresh scoped context: writes bypass EF tracking.
/// Timestamps describe observations, not when a queued database write eventually executes.
/// </summary>
public static class DiscordServerLifecycle
{
    public static async Task MarkPresentAsync(NinjaBotEntities db, DiscordServer server, DateTime now)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({server.ServerId})");
        // Upsert after the lock, including when a preceding purge removed the old row.
        // Older snapshots cannot overwrite newer presence, or a same/newer departure.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "DiscordServers" ("ServerId", "ServerName", "OwnerId", "OwnerName", "BotPresent", "JoinedAt", "LeftAt")
            VALUES ({server.ServerId}, {server.ServerName}, {server.OwnerId}, {server.OwnerName}, TRUE, {now}, NULL)
            ON CONFLICT ("ServerId") DO UPDATE SET
                "ServerName" = EXCLUDED."ServerName", "OwnerId" = EXCLUDED."OwnerId", "OwnerName" = EXCLUDED."OwnerName",
                "BotPresent" = TRUE, "JoinedAt" = EXCLUDED."JoinedAt", "LeftAt" = NULL
            WHERE ("DiscordServers"."JoinedAt" IS NULL OR "DiscordServers"."JoinedAt" <= EXCLUDED."JoinedAt")
                AND ("DiscordServers"."LeftAt" IS NULL OR "DiscordServers"."LeftAt" < EXCLUDED."JoinedAt")
            """);
        await transaction.CommitAsync();
    }

    public static async Task MarkAbsentAsync(NinjaBotEntities db, long serverId, DateTime now, DateTime? observedJoinedAt = null,
        DateTime? precedingPresenceAt = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({serverId})");
        // Duplicate leaves preserve the first departure. Reconciliation must also match
        // the presence generation it read; a delayed leave cannot erase a newer rejoin.
        if (observedJoinedAt.HasValue)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "DiscordServers" SET "BotPresent" = FALSE, "LeftAt" = {now}
                WHERE "ServerId" = {serverId} AND "BotPresent" AND "JoinedAt" = {observedJoinedAt.Value}
                    AND "JoinedAt" <= {now}
                """);
        else if (precedingPresenceAt.HasValue && precedingPresenceAt.Value <= now)
            // Carry an actually captured presence through a leave that overtakes its DB write.
            // A newer presence after the stored departure starts a new absence period even
            // if BotPresent is already false. Reused evidence is a duplicate, not a rejoin.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "DiscordServers" ("ServerId", "BotPresent", "JoinedAt", "LeftAt")
                VALUES ({serverId}, FALSE, {precedingPresenceAt.Value}, {now})
                ON CONFLICT ("ServerId") DO UPDATE SET
                    "BotPresent" = FALSE, "LeftAt" = EXCLUDED."LeftAt",
                    "JoinedAt" = CASE WHEN "DiscordServers"."JoinedAt" IS NULL
                        OR "DiscordServers"."JoinedAt" < EXCLUDED."JoinedAt"
                        THEN EXCLUDED."JoinedAt" ELSE "DiscordServers"."JoinedAt" END
                WHERE ("DiscordServers"."JoinedAt" IS NULL OR "DiscordServers"."JoinedAt" <= EXCLUDED."LeftAt")
                    AND ("DiscordServers"."LeftAt" IS NULL OR "DiscordServers"."LeftAt" < EXCLUDED."LeftAt")
                    AND ("DiscordServers"."BotPresent" OR "DiscordServers"."LeftAt" IS NULL
                        OR "DiscordServers"."LeftAt" < EXCLUDED."JoinedAt")
                """);
        else
            // A leave can arrive before the first snapshot has inserted this server.
            // Retain its fence rather than allowing that delayed snapshot to resurrect it.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "DiscordServers" ("ServerId", "BotPresent", "LeftAt")
                VALUES ({serverId}, FALSE, {now})
                ON CONFLICT ("ServerId") DO UPDATE SET "BotPresent" = FALSE, "LeftAt" = EXCLUDED."LeftAt"
                WHERE "DiscordServers"."BotPresent"
                    AND ("DiscordServers"."JoinedAt" IS NULL OR "DiscordServers"."JoinedAt" <= EXCLUDED."LeftAt")
                """);
        await transaction.CommitAsync();
    }
}
