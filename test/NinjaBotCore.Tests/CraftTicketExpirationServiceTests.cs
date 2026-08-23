using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class CraftTicketExpirationServiceTests
    {
        [Fact]
        public async Task ExpirationWorkerDeletesExpiredPendingProfessionWhenNoActiveTicketExists()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<NinjaBotEntities>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;
            await using (var seed = new NinjaBotEntities(options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.CraftTickets.Add(new CraftTicket
                {
                    ItemName = "Consecrated Alloy",
                    Status = "PendingProfession",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-20),
                    ExpiresAt = DateTime.UtcNow.AddMinutes(-5),
                    RequesterId = 1,
                    RequesterName = "Requester",
                    GuildId = 2,
                    ChannelId = 3
                });
                await seed.SaveChangesAsync();
            }

            using var provider = new ServiceCollection()
                .AddScoped(_ => new NinjaBotEntities(options))
                .BuildServiceProvider();
            using var client = new DiscordShardedClient();
            using var service = new CraftTicketExpirationService(
                NullLogger<CraftTicketExpirationService>.Instance,
                provider.GetRequiredService<IServiceScopeFactory>(),
                client);
            var check = typeof(CraftTicketExpirationService).GetMethod(
                "CheckExpiredTicketsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var checkTask = Assert.IsAssignableFrom<Task>(
                check.Invoke(service, new object[] { CancellationToken.None }));
            await checkTask;

            await using var verify = new NinjaBotEntities(options);
            Assert.Empty(await verify.CraftTickets.AsNoTracking().ToListAsync());
        }
    }
}
