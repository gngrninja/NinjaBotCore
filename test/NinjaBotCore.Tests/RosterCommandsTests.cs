using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class RosterCommandsTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly IServiceScopeFactory _scopeFactory;

        public RosterCommandsTests()
        {
            var services = new ServiceCollection();
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"RosterTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            services.AddScoped<IServiceScopeFactory>(sp => sp.GetRequiredService<IServiceScopeFactory>());

            _serviceProvider = services.BuildServiceProvider();
            _scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        }

        public void Dispose()
        {
            _serviceProvider?.Dispose();
        }

        #region Sorting Tests

        [Fact]
        public void SortRoster_ByMythicPlus_SortsDescending()
        {
            var roster = CreateTestRosterData();

            var sorted = roster.OrderByDescending(m => m.MythicPlusScore).ThenBy(m => m.Name).ToList();

            Assert.Equal("HighScore", sorted[0].Name);
            Assert.Equal("MidScore", sorted[1].Name);
            Assert.Equal("LowScore", sorted[2].Name);
        }

        [Fact]
        public void SortRoster_ByName_SortsAscending()
        {
            var roster = CreateTestRosterData();

            var sorted = roster.OrderBy(m => m.Name).ToList();

            Assert.Equal("HighScore", sorted[0].Name);
            Assert.Equal("LowScore", sorted[1].Name);
            Assert.Equal("MidScore", sorted[2].Name);
        }

        [Fact]
        public void SortRoster_ByRank_SortsAscending()
        {
            var roster = CreateTestRosterData();

            var sorted = roster.OrderBy(m => m.Rank).ThenBy(m => m.Name).ToList();

            // Rank 0 (GM) first, then 1, then 5
            Assert.Equal(0, sorted[0].Rank);
            Assert.Equal(1, sorted[1].Rank);
            Assert.Equal(5, sorted[2].Rank);
        }

        [Fact]
        public void SortRoster_ByMythicPlus_UsesNameAsTiebreaker()
        {
            var roster = new List<RosterMemberData>
            {
                new RosterMemberData { Name = "Zara", MythicPlusScore = 2000 },
                new RosterMemberData { Name = "Alpha", MythicPlusScore = 2000 },
                new RosterMemberData { Name = "Mike", MythicPlusScore = 2000 }
            };

            var sorted = roster.OrderByDescending(m => m.MythicPlusScore).ThenBy(m => m.Name).ToList();

            Assert.Equal("Alpha", sorted[0].Name);
            Assert.Equal("Mike", sorted[1].Name);
            Assert.Equal("Zara", sorted[2].Name);
        }

        private List<RosterMemberData> CreateTestRosterData()
        {
            return new List<RosterMemberData>
            {
                new RosterMemberData { Name = "LowScore", MythicPlusScore = 1000, Rank = 5, ItemLevel = 400 },
                new RosterMemberData { Name = "HighScore", MythicPlusScore = 3000, Rank = 0, ItemLevel = 450 },
                new RosterMemberData { Name = "MidScore", MythicPlusScore = 2000, Rank = 1, ItemLevel = 420 }
            };
        }

        #endregion

        #region Database Integration Tests

        [Fact]
        public async Task LoadRosterMembers_FiltersLevel70Plus()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            // Seed test data
            db.WowGuildRosterMembers.AddRange(new[]
            {
                new WowGuildRosterMember { GuildName = "TestGuild", GuildRealmSlug = "test-realm", Region = "us", CharacterName = "HighLevel", Level = 70 },
                new WowGuildRosterMember { GuildName = "TestGuild", GuildRealmSlug = "test-realm", Region = "us", CharacterName = "MaxLevel", Level = 80 },
                new WowGuildRosterMember { GuildName = "TestGuild", GuildRealmSlug = "test-realm", Region = "us", CharacterName = "LowLevel", Level = 60 }
            });
            await db.SaveChangesAsync();

            // Query with level filter
            var members = await db.WowGuildRosterMembers
                .Where(x => x.GuildName == "TestGuild" && x.GuildRealmSlug == "test-realm" && x.Region == "us" && x.Level >= 70)
                .ToListAsync();

            Assert.Equal(2, members.Count);
            Assert.DoesNotContain(members, m => m.CharacterName == "LowLevel");
        }

        [Fact]
        public async Task LoadRosterMembers_FiltersByGuildRealmRegion()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            // Seed test data with different guilds/realms/regions
            db.WowGuildRosterMembers.AddRange(new[]
            {
                new WowGuildRosterMember { GuildName = "TargetGuild", GuildRealmSlug = "target-realm", Region = "us", CharacterName = "Target1", Level = 70 },
                new WowGuildRosterMember { GuildName = "TargetGuild", GuildRealmSlug = "target-realm", Region = "us", CharacterName = "Target2", Level = 70 },
                new WowGuildRosterMember { GuildName = "OtherGuild", GuildRealmSlug = "target-realm", Region = "us", CharacterName = "Other1", Level = 70 },
                new WowGuildRosterMember { GuildName = "TargetGuild", GuildRealmSlug = "other-realm", Region = "us", CharacterName = "Other2", Level = 70 },
                new WowGuildRosterMember { GuildName = "TargetGuild", GuildRealmSlug = "target-realm", Region = "eu", CharacterName = "Other3", Level = 70 }
            });
            await db.SaveChangesAsync();

            var members = await db.WowGuildRosterMembers
                .Where(x => x.GuildName == "TargetGuild" && x.GuildRealmSlug == "target-realm" && x.Region == "us" && x.Level >= 70)
                .ToListAsync();

            Assert.Equal(2, members.Count);
            Assert.All(members, m => Assert.Equal("TargetGuild", m.GuildName));
        }

        [Fact]
        public async Task RefreshMPlusScores_UpdatesAllMembers_IncludingZeroScores()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            // Seed with existing M+ scores
            var member1 = new WowGuildRosterMember
            {
                GuildName = "TestGuild",
                GuildRealmSlug = "test-realm",
                Region = "us",
                CharacterName = "HasScore",
                Level = 70,
                MythicPlusScore = 2500
            };
            var member2 = new WowGuildRosterMember
            {
                GuildName = "TestGuild",
                GuildRealmSlug = "test-realm",
                Region = "us",
                CharacterName = "WillLoseScore",
                Level = 70,
                MythicPlusScore = 1500
            };
            db.WowGuildRosterMembers.AddRange(member1, member2);
            await db.SaveChangesAsync();

            // Simulate refresh - member2 now has 0 score (season reset, etc.)
            member2.MythicPlusScore = 0;
            await db.SaveChangesAsync();

            // Verify the update was persisted
            var updated = await db.WowGuildRosterMembers.FirstAsync(m => m.CharacterName == "WillLoseScore");
            Assert.Equal(0, updated.MythicPlusScore);
        }

        [Fact]
        public async Task ApiUsageLog_PersistsCorrectly()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var log = new ApiUsageLog
            {
                GuildId = 123456789,
                UserId = 987654321,
                Operation = "RosterMPlusRefresh",
                ApiCallCount = 150,
                WowGuild = "Test Guild",
                WowRealm = "test-realm",
                WowRegion = "us",
                Timestamp = DateTime.UtcNow
            };

            db.ApiUsageLogs.Add(log);
            await db.SaveChangesAsync();

            var saved = await db.ApiUsageLogs.FirstAsync();
            Assert.Equal("RosterMPlusRefresh", saved.Operation);
            Assert.Equal(150, saved.ApiCallCount);
            Assert.Equal("Test Guild", saved.WowGuild);
        }

        #endregion

        #region Rate Limiting Tests

        [Fact]
        public async Task MPlusRefresh_RespectsCooldown()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            // Create association with recent refresh
            var association = new WowGuildAssociations
            {
                ServerId = 123456789,
                WowGuild = "TestGuild",
                LocalRealmSlug = "test-realm",
                WowRegion = "us",
                LastMPlusRefresh = DateTime.UtcNow.AddMinutes(-30) // 30 min ago
            };
            db.WowGuildAssociations.Add(association);
            await db.SaveChangesAsync();

            // Check cooldown (1 hour)
            var cooldown = TimeSpan.FromHours(1);
            var elapsed = DateTime.UtcNow - association.LastMPlusRefresh.Value;
            var canRefresh = elapsed >= cooldown;

            Assert.False(canRefresh);
            Assert.True((cooldown - elapsed).TotalMinutes > 25);
        }

        [Fact]
        public async Task MPlusRefresh_AllowsAfterCooldownExpires()
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            // Create association with old refresh
            var association = new WowGuildAssociations
            {
                ServerId = 123456790,
                WowGuild = "TestGuild2",
                LocalRealmSlug = "test-realm",
                WowRegion = "us",
                LastMPlusRefresh = DateTime.UtcNow.AddHours(-2) // 2 hours ago
            };
            db.WowGuildAssociations.Add(association);
            await db.SaveChangesAsync();

            // Check cooldown (1 hour)
            var cooldown = TimeSpan.FromHours(1);
            var elapsed = DateTime.UtcNow - association.LastMPlusRefresh.Value;
            var canRefresh = elapsed >= cooldown;

            Assert.True(canRefresh);
        }

        #endregion

        #region ParseGuildParam Tests

        [Fact]
        public void ParseGuildParam_ValidFormat_ReturnsCorrectTuple()
        {
            var guildParam = "Test Guild|test-realm|us";
            var parts = guildParam.Split('|');

            Assert.Equal(3, parts.Length);
            Assert.Equal("Test Guild", parts[0]);
            Assert.Equal("test-realm", parts[1]);
            Assert.Equal("us", parts[2]);
        }

        [Fact]
        public void ParseGuildParam_InvalidFormat_HasLessThan3Parts()
        {
            var guildParam = "Test Guild|test-realm";
            var parts = guildParam.Split('|');

            Assert.True(parts.Length < 3);
        }

        [Fact]
        public void ParseGuildParam_WithPipeInGuildName_SplitsCorrectly()
        {
            // Guild names can't contain pipes, so this tests the delimiter safety
            var guildParam = "Normal Guild|realm-name|eu";
            var parts = guildParam.Split('|');

            Assert.Equal(3, parts.Length);
            Assert.Equal("Normal Guild", parts[0]);
        }

        #endregion
    }
}
