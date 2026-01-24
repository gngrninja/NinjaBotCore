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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RealmWatchSubscription>()
            .ToTable("RealmWatchSubscriptions");

        modelBuilder.Entity<RealmStatusCache>()
            .ToTable("RealmStatusCache");
    }
}
