using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using Xunit;

namespace NinjaBotCore.Tests
{
    [Collection("DatabaseConfigurator")]
    public class ChannelCheckTests : IDisposable
    {
        private readonly string _dbPath;

        public ChannelCheckTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"channelcheck-{Guid.NewGuid()}.db");

            var configData = new Dictionary<string, string>
            {
                ["Database:Provider"] = "sqlite",
                ["ConnectionStrings:NinjaBot"] = $"Data Source={_dbPath}"
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            DatabaseConfigurator.ConfigureFrom(configuration);

            // Initialize schema for ChannelOutputs table
            using var db = new NinjaBotEntities();
            db.Database.EnsureCreated();
        }

        [Fact]
        public async Task SetGuildBotChannelAsync_ShouldCreateAndReturnChannel()
        {
            var channelCheck = new ChannelCheck();

            await channelCheck.SetGuildBotChannelAsync(
                channelId: 123,
                channelName: "general",
                userId: 456,
                userName: "tester",
                guildName: "TestGuild",
                guildId: 789);

            var channel = channelCheck.GetGuildBotChannel(789);

            Assert.NotNull(channel);
            Assert.Equal(123, channel.ChannelId);
            Assert.Equal("general", channel.ChannelName);
            Assert.Equal(789, channel.ServerId);
        }

        [Fact]
        public async Task SetGuildBotChannelAsync_ShouldUpdateExistingChannel()
        {
            var channelCheck = new ChannelCheck();

            await channelCheck.SetGuildBotChannelAsync(123, "general", 1, "tester", "TestGuild", 789);
            await channelCheck.SetGuildBotChannelAsync(321, "bot-updates", 1, "tester", "TestGuild", 789);

            var channel = channelCheck.GetGuildBotChannel(789);

            Assert.NotNull(channel);
            Assert.Equal(321, channel.ChannelId);
            Assert.Equal("bot-updates", channel.ChannelName);
        }

        [Fact]
        public void GetGuildBotChannel_WhenNoneSet_ReturnsEmptyObject()
        {
            var channelCheck = new ChannelCheck();

            var channel = channelCheck.GetGuildBotChannel(111);

            Assert.NotNull(channel);
            Assert.Equal(0L, channel.ChannelId ?? 0);
            Assert.True(string.IsNullOrEmpty(channel.ChannelName));
        }

        public void Dispose()
        {
            if (File.Exists(_dbPath))
            {
                try
                {
                    File.Delete(_dbPath);
                }
                catch
                {
                    // ignore cleanup failures
                }
            }
        }
    }
}
