using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotHelpers.Configuration;
using NinjaBotHelpers.Discord;
using NinjaBotHelpers.Retention;
using NinjaBotHelpers.Workers;
using Xunit;

namespace NinjaBotHelpers.Tests.Retention;

[Collection("ServerRetention")]
public class ServerRetentionWorkerTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(Now); }
    private static ServerRetentionWorker Worker(HelpersConfiguration config, IDiscordMembershipClient client) =>
        new(config, new DepartedServerPurger(config.ConnectionString, client), NullLogger<ServerRetentionWorker>.Instance, new Clock());

    [Fact]
    public async Task Defaults_are_disabled_and_dry_run_with_no_DB_or_HTTP_work()
    {
        var config = new HelpersConfiguration { ConnectionString = "deliberately invalid" };
        Assert.False(config.ServerRetention.Enabled); Assert.True(config.ServerRetention.DryRun);
        using var w = Worker(config, new DepartedServerPurgerTests.Membership((_, _) => throw new Exception("Unexpected HTTP")));
        Assert.Equal(0, await w.RunOnceAsync(default));
        await w.StartAsync(default); await w.StopAsync(default);
    }

    [Fact]
    public void Configuration_binding_and_DI_register_real_worker_without_starting_it()
    {
        var source = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServerRetention:Enabled"] = "true", ["ServerRetention:DryRun"] = "false",
            ["ServerRetention:CheckIntervalMinutes"] = "120", ["ServerRetention:BatchSize"] = "7"
        }).Build();
        var config = new HelpersConfiguration();
        config.ServerRetention = ServerRetentionSettings.Load(source);
        Assert.True(config.ServerRetention.Enabled); Assert.False(config.ServerRetention.DryRun);
        Assert.Equal(120, config.ServerRetention.CheckIntervalMinutes); Assert.Equal(7, config.ServerRetention.BatchSize);
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(config);
        services.AddServerRetention(config);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<ServerRetentionWorker>(Assert.Single(provider.GetServices<IHostedService>()));
    }

    [Theory]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "101")]
    [InlineData("CheckIntervalMinutes", "0")]
    [InlineData("CheckIntervalMinutes", "1441")]
    [InlineData("InitialDelaySeconds", "-1")]
    [InlineData("Enabled", "invalid")]
    public void Unsafe_configuration_is_rejected(string name, string value)
    {
        var source = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"ServerRetention:{name}"] = value }).Build();
        Assert.ThrowsAny<Exception>(() => ServerRetentionSettings.Load(source));
    }

    [RetentionPostgresFact]
    public async Task Batches_are_bounded_advance_past_unknown_servers_and_wrap()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await using (var db = f.Context())
        {
            foreach (long id in new long[] { 101, 102, 103 }) db.DiscordServers.Add(new DiscordServer { ServerId = id, BotPresent = false, LeftAt = Now.AddDays(-31) });
            await db.SaveChangesAsync();
        }
        var seen = new List<long>();
        var m = new DepartedServerPurgerTests.Membership((id, _) => { seen.Add(id); return Task.FromResult(GuildMembership.Unknown); });
        var config = new HelpersConfiguration { ConnectionString = f.ConnectionString };
        config.ServerRetention.Enabled = true; config.ServerRetention.BatchSize = 2;
        using var w = Worker(config, m);
        Assert.Equal(2, await w.RunOnceAsync(default)); Assert.Equal(new long[] { 101, 102 }, seen);
        Assert.Equal(1, await w.RunOnceAsync(default)); Assert.Equal(new long[] { 101, 102, 103 }, seen);
        Assert.Equal(2, await w.RunOnceAsync(default)); Assert.Equal(new long[] { 101, 102, 103, 101, 102 }, seen);
        Assert.Equal(3, await f.Count("DiscordServers"));
    }

    [RetentionPostgresFact]
    public async Task One_server_failure_does_not_abort_remaining_batch_and_dry_run_preserves_data()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await using (var db = f.Context())
        {
            foreach (long id in new long[] { 101, 102 }) db.DiscordServers.Add(new DiscordServer { ServerId = id, BotPresent = false, LeftAt = Now.AddDays(-31) });
            await db.SaveChangesAsync();
        }
        var seen = new List<long>();
        var m = new DepartedServerPurgerTests.Membership((id, _) => { seen.Add(id); if (id == 101) throw new HttpRequestException("synthetic"); return Task.FromResult(GuildMembership.Absent); });
        var config = new HelpersConfiguration { ConnectionString = f.ConnectionString };
        config.ServerRetention.Enabled = true;
        using var w = Worker(config, m);
        Assert.Equal(2, await w.RunOnceAsync(default)); Assert.Equal(new long[] { 101, 102 }, seen);
        Assert.Equal(2, await f.Count("DiscordServers"));
        config.ServerRetention.DryRun = false;
        Assert.Equal(2, await w.RunOnceAsync(default)); Assert.Equal(1, await f.Count("DiscordServers"));
    }

    [Fact]
    public async Task Cancellation_stops_initial_delay_without_opening_database()
    {
        var config = new HelpersConfiguration { ConnectionString = "deliberately invalid" };
        config.ServerRetention.Enabled = true;
        using var w = Worker(config, new DepartedServerPurgerTests.Membership((_, _) => throw new Exception("Unexpected HTTP")));
        await w.StartAsync(default);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await w.StopAsync(stop.Token);
        Assert.True(w.ExecuteTask!.IsCompleted);
    }
}
