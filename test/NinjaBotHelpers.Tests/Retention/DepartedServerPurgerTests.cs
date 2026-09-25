using Microsoft.EntityFrameworkCore;
using NinjaBotCore.Database;
using NinjaBotHelpers.Discord;
using NinjaBotHelpers.Retention;
using Xunit;

namespace NinjaBotHelpers.Tests.Retention;

[Collection("ServerRetention")]
public class DepartedServerPurgerTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    internal sealed class Membership(Func<long, CancellationToken, Task<GuildMembership>> check) : IDiscordMembershipClient
    {
        public int Calls;
        public Task<GuildMembership> CheckAsync(long id, CancellationToken ct) { Calls++; return check(id, ct); }
    }
    private static Membership Absent() => new((_, _) => Task.FromResult(GuildMembership.Absent));
    private static async Task Seed(RetentionDatabase fixture, long id, bool present, DateTime? left)
    {
        await using var db = fixture.Context();
        db.DiscordServers.Add(new DiscordServer { ServerId = id, ServerName = "same-name", BotPresent = present, LeftAt = left });
        db.Notes.Add(new Note { ServerId = id });
        await db.SaveChangesAsync();
    }

    [RetentionPostgresFact]
    public async Task Exact_30_day_boundary_deletes_only_eligible_servers_and_is_restart_safe()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await Seed(f, 101, false, Now.AddDays(-30));
        await Seed(f, 102, false, Now.AddDays(-30).AddTicks(10));
        await Seed(f, 103, true, Now.AddDays(-40));
        await Seed(f, 104, false, null);
        await Seed(f, 105, false, Now.AddDays(1));
        var p = new DepartedServerPurger(f.ConnectionString, Absent());
        var candidates = await p.FindCandidatesAsync(Now, 0, 25, default);
        Assert.Equal(new long[] { 101 }, candidates.Select(x => x.ServerId));
        Assert.Equal(PurgeResult.Deleted, await p.PurgeAsync(candidates.Single(), Now, false, default));
        Assert.Empty(await new DepartedServerPurger(f.ConnectionString, Absent()).FindCandidatesAsync(Now, 0, 25, default));
        Assert.Equal(4, await f.Count("Notes"));
        Assert.Equal(4, await f.Count("DiscordServers"));
    }

    [RetentionPostgresFact]
    public async Task Dry_run_and_uncertain_or_present_membership_preserve_every_row()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await Seed(f, 101, false, Now.AddDays(-31));
        var c = new DepartedServerCandidate(101, Now.AddDays(-31));
        Assert.Equal(PurgeResult.DryRun, await new DepartedServerPurger(f.ConnectionString, Absent()).PurgeAsync(c, Now, true, default));
        foreach (var membership in new[] { GuildMembership.Unknown, GuildMembership.Present })
        {
            var p = new DepartedServerPurger(f.ConnectionString, new Membership((_, _) => Task.FromResult(membership)));
            Assert.Equal(PurgeResult.MembershipNotAbsent, await p.PurgeAsync(c, Now, false, default));
        }
        Assert.Equal(1, await f.Count("Notes")); Assert.Equal(1, await f.Count("DiscordServers"));
    }

    [RetentionPostgresFact]
    public async Task Observed_rejoin_cancels_stale_departure_clock_even_if_gateway_missed_it()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await Seed(f, 101, false, Now.AddDays(-31));
        var present = new Membership((_, _) => Task.FromResult(GuildMembership.Present));
        var p = new DepartedServerPurger(f.ConnectionString, present);
        Assert.Equal(PurgeResult.MembershipNotAbsent, await p.PurgeAsync(new(101, Now.AddDays(-31)), Now, true, default));
        Assert.Single(await p.FindCandidatesAsync(Now, 0, 25, default)); // dry-run cannot repair
        Assert.Equal(PurgeResult.MembershipNotAbsent, await p.PurgeAsync(new(101, Now.AddDays(-31)), Now, false, default));
        Assert.Empty(await p.FindCandidatesAsync(Now.AddDays(1), 0, 25, default));
        await using var db = f.Context();
        var row = await db.DiscordServers.AsNoTracking().SingleAsync();
        Assert.True(row.BotPresent); Assert.Null(row.LeftAt);
        Assert.Equal(1, await f.Count("Notes"));
    }

    [RetentionPostgresFact]
    public async Task Changed_departure_or_committed_rejoin_invalidates_candidate_before_HTTP()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await Seed(f, 101, false, Now.AddDays(-31));
        var m = Absent(); var p = new DepartedServerPurger(f.ConnectionString, m);
        Assert.Equal(PurgeResult.Ineligible, await p.PurgeAsync(new(101, Now.AddDays(-40)), Now, false, default));
        await f.Sql("UPDATE \"DiscordServers\" SET \"BotPresent\" = true, \"LeftAt\" = NULL");
        Assert.Equal(PurgeResult.Ineligible, await p.PurgeAsync(new(101, Now.AddDays(-31)), Now, false, default));
        Assert.Equal(0, m.Calls); Assert.Equal(1, await f.Count("Notes"));
    }

    [RetentionPostgresFact]
    public async Task Late_membership_change_rolls_back_all_deletes()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await Seed(f, 101, false, Now.AddDays(-31));
        int calls = 0;
        var m = new Membership((_, _) => Task.FromResult(++calls == 1 ? GuildMembership.Absent : GuildMembership.Present));
        Assert.Equal(PurgeResult.MembershipNotAbsent, await new DepartedServerPurger(f.ConnectionString, m).PurgeAsync(new(101, Now.AddDays(-31)), Now, false, default));
        Assert.Equal(2, calls); Assert.Equal(1, await f.Count("Notes")); Assert.Equal(1, await f.Count("DiscordServers"));
        await using var db = f.Context();
        var rejoined = await db.DiscordServers.AsNoTracking().SingleAsync();
        Assert.True(rejoined.BotPresent); Assert.Null(rejoined.LeftAt);
    }

    [RetentionPostgresFact]
    public async Task Database_failure_rolls_back_previously_deleted_rows()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await Seed(f, 101, false, Now.AddDays(-31));
        await f.Sql("""
            CREATE FUNCTION fail_retention() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic failure'; END $$;
            CREATE TRIGGER fail_retention BEFORE DELETE ON "DiscordServers" FOR EACH ROW EXECUTE FUNCTION fail_retention();
            """);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => new DepartedServerPurger(f.ConnectionString, Absent()).PurgeAsync(new(101, Now.AddDays(-31)), Now, false, default));
        Assert.Equal(1, await f.Count("Notes")); Assert.Equal(1, await f.Count("DiscordServers"));
    }

    [RetentionPostgresFact]
    public async Task Cancellation_after_deletion_rolls_back()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await Seed(f, 101, false, Now.AddDays(-31));
        using var cts = new CancellationTokenSource(); int calls = 0;
        var m = new Membership((_, ct) => { if (++calls == 2) { cts.Cancel(); ct.ThrowIfCancellationRequested(); } return Task.FromResult(GuildMembership.Absent); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DepartedServerPurger(f.ConnectionString, m).PurgeAsync(new(101, Now.AddDays(-31)), Now, false, cts.Token));
        Assert.Equal(1, await f.Count("Notes")); Assert.Equal(1, await f.Count("DiscordServers"));
    }

    [RetentionPostgresFact]
    public async Task Inventory_matches_core_model_and_deletes_exact_owner_with_children()
    {
        await using var f = new RetentionDatabase(); await f.InitializeAsync();
        await Seed(f, 101, false, Now.AddDays(-31));
        await Seed(f, 202, true, null);
        await using var db = f.Context();
        var owned = db.Model.GetEntityTypes().Select(e => new { Entity = e, Owner = e.GetProperties().SingleOrDefault(p => new[] { "ServerId", "ServerID", "GuildId", "DiscordGuildId" }.Contains(p.Name)) })
            .Where(x => x.Owner != null && x.Entity.ClrType != typeof(DiscordServer)).ToList();
        Assert.Equal(owned.Select(x => (x.Entity.GetTableName()!, x.Owner!.GetColumnName())).OrderBy(x => x.Item1), DepartedServerPurger.DirectTables.OrderBy(x => x.Table));
        foreach (var x in owned.Where(x => x.Entity.ClrType != typeof(Note)))
        {
            foreach (long id in new long[] { 101, 202 })
            {
                var entity = Activator.CreateInstance(x.Entity.ClrType)!;
                foreach (var prop in x.Entity.GetProperties().Where(p => !p.IsNullable && !p.IsPrimaryKey()))
                {
                    if (prop.ClrType == typeof(string)) prop.PropertyInfo!.SetValue(entity, "fixture");
                    if (prop.ClrType == typeof(DateTime)) prop.PropertyInfo!.SetValue(entity, Now);
                }
                x.Owner!.PropertyInfo!.SetValue(entity, id);
                db.Add(entity);
            }
        }
        db.WowCharAssociation.Add(new WowCharAssociation { ServerId = null, UserId = 999 });
        db.UserPushGroupSettings.Add(new UserPushGroupSettings { UserId = 999 });
        db.WowGuildRosterMembers.Add(new WowGuildRosterMember { GuildName = "fixture", LastUpdated = Now });
        await db.SaveChangesAsync();
        foreach (var poll in await db.Polls.ToListAsync())
        {
            var option = new PollOption { PollId = poll.Id, OptionText = "fixture" }; db.PollOptions.Add(option);
            await db.SaveChangesAsync();
            db.PollVotes.Add(new PollVote { PollId = poll.Id, OptionId = option.Id, PollOption = option, UserId = 999, VotedAt = Now });
        }
        foreach (var group in await db.PushGroups.ToListAsync())
            db.PushGroupSignups.Add(new PushGroupSignup { PushGroupId = group.Id, UserId = 999, SignedUpAt = Now });
        await db.SaveChangesAsync();
        Assert.Equal(PurgeResult.Deleted, await new DepartedServerPurger(f.ConnectionString, Absent()).PurgeAsync(new(101, Now.AddDays(-31)), Now, false, default));
        foreach (var x in owned)
            Assert.Equal(x.Entity.ClrType == typeof(WowCharAssociation) ? 2 : 1, await f.Count(x.Entity.GetTableName()!));
        foreach (string table in new[] { "PollOptions", "PollVotes", "PushGroupSignups", "UserPushGroupSettings", "WowGuildRosterMembers", "DiscordServers" }) Assert.Equal(1, await f.Count(table));
    }
}
