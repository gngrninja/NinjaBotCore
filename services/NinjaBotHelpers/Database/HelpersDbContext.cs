using Microsoft.EntityFrameworkCore;

namespace NinjaBotHelpers.Database;

/// <summary>
/// Database context for NinjaBotHelpers
/// Shares the same database as NinjaBotCore but only maps tables needed by helpers
/// </summary>
public class HelpersDbContext : DbContext
{
    public HelpersDbContext(DbContextOptions<HelpersDbContext> options) : base(options)
    {
    }

    public DbSet<RealmWatchSubscription> RealmWatchSubscriptions { get; set; } = null!;
    public DbSet<RealmStatusCache> RealmStatusCache { get; set; } = null!;

    // WoW static data tables (shared with NinjaBotCore)
    public DbSet<WowAchievements> WowAchievements { get; set; } = null!;
    public DbSet<WowAchievementCriteria> WowAchievementCriteria { get; set; } = null!;
    public DbSet<WowPets> WowPets { get; set; } = null!;
    public DbSet<WowMounts> WowMounts { get; set; } = null!;
    public DbSet<WowItems> WowItems { get; set; } = null!;

    // Sync control tables (shared with NinjaBotCore)
    public DbSet<StaticDataSyncRequest> StaticDataSyncRequests { get; set; } = null!;
    public DbSet<StaticDataSyncStatus> StaticDataSyncStatus { get; set; } = null!;

    // WarcraftLogs monitoring tables (shared with NinjaBotCore)
    public DbSet<LogMonitoring> LogMonitoring { get; set; } = null!;
    public DbSet<WclPosted> WclPosted { get; set; } = null!;
    public DbSet<WowGuildAssociations> WowGuildAssociations { get; set; } = null!;
    public DbSet<WowClassicGuild> WowClassicGuild { get; set; } = null!;
    public DbSet<WowVanillaGuild> WowVanillaGuild { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RealmWatchSubscription>()
            .ToTable("RealmWatchSubscriptions");

        modelBuilder.Entity<RealmStatusCache>()
            .ToTable("RealmStatusCache");

        // WoW static data tables (shared with NinjaBotCore)
        modelBuilder.Entity<WowAchievements>()
            .ToTable("WowAchievements");

        modelBuilder.Entity<WowAchievementCriteria>()
            .ToTable("WowAchievementCriteria");

        modelBuilder.Entity<WowPets>()
            .ToTable("WowPets");

        modelBuilder.Entity<WowMounts>()
            .ToTable("WowMounts");

        modelBuilder.Entity<WowItems>()
            .ToTable("WowItems");

        // Sync control tables (shared with NinjaBotCore)
        modelBuilder.Entity<StaticDataSyncRequest>()
            .ToTable("StaticDataSyncRequests");

        modelBuilder.Entity<StaticDataSyncStatus>()
            .ToTable("StaticDataSyncStatus");

        // WarcraftLogs monitoring tables (shared with NinjaBotCore)
        modelBuilder.Entity<LogMonitoring>()
            .ToTable("LogMonitoring");

        modelBuilder.Entity<WclPosted>()
            .ToTable("WclPosted");

        modelBuilder.Entity<WowGuildAssociations>()
            .ToTable("WowGuildAssociations");

        modelBuilder.Entity<WowClassicGuild>()
            .ToTable("WowClassicGuild");

        modelBuilder.Entity<WowVanillaGuild>()
            .ToTable("WowVanillaGuild");
    }
}
