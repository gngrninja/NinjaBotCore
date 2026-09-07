using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;
namespace NinjaBotCore.Tests;
public class RaidRecapGuildTests
{
    private sealed class GuildDatabase : IDisposable
    {
        // Own EF's provider rather than enter its process-wide cache. Other test
        // fixtures can exceed that cache's 20-provider warning threshold before
        // these tests run. All scopes here still share one isolated in-memory store.
        private readonly ServiceProvider _efServices = new ServiceCollection()
            .AddEntityFrameworkInMemoryDatabase()
            .BuildServiceProvider();

        public ServiceProvider Services { get; }

        public GuildDatabase(string name = null)
        {
            var database = name ?? Guid.NewGuid().ToString();
            Services = new ServiceCollection()
                .AddDbContext<NinjaBotEntities>(options => options
                    .UseInMemoryDatabase(database)
                    .UseInternalServiceProvider(_efServices))
                .BuildServiceProvider();
        }

        public void Dispose()
        {
            try { Services.Dispose(); }
            finally { _efServices.Dispose(); }
        }
    }

    [Fact]
    public void GuildDatabaseOwnsItsInternalProviderWithoutSuppressingWarnings()
    {
        using var database = new GuildDatabase();
        IServiceProvider internalProvider;
        using (var scope = database.Services.CreateScope())
        {
            // Inspect options before constructing a context: this regression must not
            // fill or clear EF's process-wide cache and disturb unrelated tests.
            var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<NinjaBotEntities>>();
            var core = options.FindExtension<CoreOptionsExtension>();
            internalProvider = core.InternalServiceProvider;
            Assert.NotNull(internalProvider);
            Assert.Equal(WarningBehavior.Throw,
                core.WarningsConfiguration.GetBehavior(CoreEventId.ManyServiceProvidersCreatedWarning));
        }
        using (var scope = database.Services.CreateScope())
        {
            var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<NinjaBotEntities>>();
            Assert.Same(internalProvider, options.FindExtension<CoreOptionsExtension>().InternalServiceProvider);
        }

        database.Dispose();
        Assert.Throws<ObjectDisposedException>(() => database.Services.CreateScope());
        Assert.Throws<ObjectDisposedException>(() => internalProvider.GetService(typeof(IServiceScopeFactory)));
    }

    [Fact]
    public async Task GuildDatabaseSharesDataAcrossScopesButNotAcrossFixtures()
    {
        var name = Guid.NewGuid().ToString();
        using var first = new GuildDatabase(name);
        using var second = new GuildDatabase(name);
        NinjaBotEntities seeded;
        using (var scope = first.Services.CreateScope())
        {
            seeded = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            Assert.Same(seeded, scope.ServiceProvider.GetRequiredService<NinjaBotEntities>());
            seeded.WowGuildAssociations.Add(new WowGuildAssociations { ServerId = 1, WowGuild = "One" });
            await seeded.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => seeded.WowGuildAssociations.CountAsync());

        using (var scope = first.Services.CreateScope())
        {
            var read = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            Assert.NotSame(seeded, read);
            Assert.Equal("One", (await read.WowGuildAssociations.SingleAsync()).WowGuild);
        }
        using (var scope = second.Services.CreateScope())
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<NinjaBotEntities>()
                .WowGuildAssociations.ToListAsync());
        }
    }

    private static IInteractionContext Context(ulong id,string name)
    {
        var g=new Mock<IGuild>();g.SetupGet(x=>x.Id).Returns(id);g.SetupGet(x=>x.Name).Returns(name);
        var c=new Mock<IInteractionContext>();c.SetupGet(x=>x.Guild).Returns(g.Object);return c.Object;
    }
    [Fact]
    public async Task GuildLookupUsesStableServerIdDespiteRenameAndDuplicateNames()
    {
        using var database = new GuildDatabase();
        var services = database.Services;
        using(var scope=services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            db.WowGuildAssociations.AddRange(new WowGuildAssociations {ServerId=1,ServerName="Same",WowGuild="One",WowRealm="Area 52",LocalRealmSlug="area-52",WowRegion="us"},new WowGuildAssociations {ServerId=2,ServerName="Same",WowGuild="Two",WowRealm="Different",LocalRealmSlug="different",WowRegion="eu"});
            await db.SaveChangesAsync();
        }
        var adapter=new RaidRecapDiscord(services.GetRequiredService<IServiceScopeFactory>());
        Assert.Equal(new RaidRecapGuild("One","area-52","us"),await adapter.GuildAsync(Context(1,"Renamed")));
        Assert.Equal(new RaidRecapGuild("Two","different","eu"),await adapter.GuildAsync(Context(2,"Same")));
        using var read=services.CreateScope();Assert.Equal(2,await read.ServiceProvider.GetRequiredService<NinjaBotEntities>().WowGuildAssociations.CountAsync());
    }
    [Fact]
    public async Task MissingOrAmbiguousAssociationGivesSetupGuidanceWithoutGuessing()
    {
        using var database = new GuildDatabase();
        var services = database.Services;
        var adapter=new RaidRecapDiscord(services.GetRequiredService<IServiceScopeFactory>());
        var error=await Assert.ThrowsAsync<ArgumentException>(()=>adapter.GuildAsync(Context(1,"Same")));
        Assert.Contains("/setguild",error.Message);
        using var scope=services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
        db.WowGuildAssociations.AddRange(new WowGuildAssociations {ServerId=1,WowGuild="A"},new WowGuildAssociations {ServerId=1,WowGuild="B"});await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(()=>adapter.GuildAsync(Context(1,"Same")));
    }
}
