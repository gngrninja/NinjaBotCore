using System;
using System.Linq;
using System.Reflection;
using Discord.Interactions;
using Newtonsoft.Json;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class GuildLiveRaidViewTests
    {
        [Fact]
        public void LiveRaidResponse_DeserializesAndRendersBossProgress()
        {
            const string json = """
            {
              "guild": { "id": 42, "name": "Midnight Misfits", "path": "/guilds/us/area-52/midnight-misfits" },
              "guildPrivacy": { "raidPulls": true, "raidPercents": true, "wereRaidPullsRestricted": false },
              "raid": { "id": 100, "slug": "the-venomous-abyss", "name": "The Venomous Abyss" },
              "bosses": [
                { "boss": { "id": 1, "slug": "first-boss", "name": "First Boss", "ordinal": 1 }, "bestPercent": 0, "pullCount": 4, "isDefeated": true },
                { "boss": { "id": 2, "slug": "second-boss", "name": "Second Boss", "ordinal": 2 }, "bestPercent": 23.4, "pullCount": 18, "isDefeated": false, "pullEndedAt": "2026-08-23T03:00:00Z" }
              ]
            }
            """;

            var response = JsonConvert.DeserializeObject<RaiderIOModels.GuildLiveRaidResponse>(json);
            var embed = GuildLiveRaidView.Build(response).Build();

            Assert.Equal("The Venomous Abyss", response.Raid.Name);
            Assert.Equal(18, response.Bosses[1].PullCount);
            Assert.Contains("Live Raid", embed.Title);
            Assert.Contains("✅ First Boss", embed.Description);
            Assert.Contains("23.4%", embed.Description);
            Assert.Contains("18 pulls", embed.Description);
            Assert.Contains("raider.io/guilds/us/area-52/midnight-misfits", embed.Description);
        }

        [Fact]
        public void LiveRaidRejectsOffOriginGuildPaths()
        {
            var response = new RaiderIOModels.GuildLiveRaidResponse
            {
                Guild = new RaiderIOModels.LiveGuildSummary { Name = "Guild", Path = "//evil.example/steal" },
                Raid = new RaiderIOModels.LiveRaidSummary { Name = "Raid" },
                Bosses = Array.Empty<RaiderIOModels.LiveRaidBoss>()
            };

            var embed = GuildLiveRaidView.Build(response).Build();

            Assert.DoesNotContain("evil.example", embed.Description);
        }

        [Fact]
        public void GuildCommands_ExposesUserBoundLiveRaidRoute()
        {
            var method = typeof(GuildCommands).GetMethod(
                "HandleLiveRaid",
                BindingFlags.Instance | BindingFlags.Public);
            var route = method?.GetCustomAttribute<ComponentInteractionAttribute>();

            Assert.NotNull(route);
            Assert.Equal("guild_live_raid~*", route.CustomId);
        }
    }
}
