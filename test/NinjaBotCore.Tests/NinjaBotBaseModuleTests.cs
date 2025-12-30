using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using Microsoft.EntityFrameworkCore.Storage;
using NinjaBotCore.Modules.Interactions;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class NinjaBotBaseModuleTests
    {
        private static readonly InMemoryDatabaseRoot _databaseRoot = new();
        private const string InMemoryDbName = "NinjaBotBaseModuleTests";
        private readonly IServiceProvider _provider;

        public NinjaBotBaseModuleTests()
        {
            var services = new ServiceCollection();
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase(InMemoryDbName, _databaseRoot));

            _provider = services.BuildServiceProvider();
        }

        [Fact]
        public async Task WithDbAsync_WritesAndReadsEntities()
        {
            var module = new TestModule(_provider.GetRequiredService<IServiceScopeFactory>());

            await module.AddNoteAsync("async note", 101);
            var count = await module.CountNotesAsync(101);

            Assert.Equal(1, count);
        }

        [Fact]
        public void WithDb_SynchronousAccess_Works()
        {
            var module = new TestModule(_provider.GetRequiredService<IServiceScopeFactory>());

            var count = module.AddSyncAndCount("sync note", 202);

            Assert.Equal(1, count);
        }

        private sealed class TestModule : NinjaBotBaseModule
        {
            public TestModule(IServiceScopeFactory scopeFactory) : base(scopeFactory)
            {
            }

            public Task AddNoteAsync(string text, long serverId)
            {
                return WithDbAsync(async db =>
                {
                    db.Notes.Add(new Note
                    {
                        Note1 = text,
                        ServerId = serverId,
                        ServerName = "test-server",
                        SetBy = "tester",
                        SetById = 1,
                        TimeSet = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                });
            }

            public Task<int> CountNotesAsync(long serverId)
            {
                return WithDbAsync(db => db.Notes.CountAsync(n => n.ServerId == serverId));
            }

            public int AddSyncAndCount(string text, long serverId)
            {
                return WithDb(db =>
                {
                    db.Notes.Add(new Note
                    {
                        Note1 = text,
                        ServerId = serverId,
                        ServerName = "test-server",
                        SetBy = "tester",
                        SetById = 1,
                        TimeSet = DateTime.UtcNow
                    });
                    db.SaveChanges();
                    return db.Notes.Count(n => n.ServerId == serverId);
                });
            }
        }
    }
}
