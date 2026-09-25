using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using Npgsql;
using Xunit;

namespace NinjaBotHelpers.Tests.Retention;

[CollectionDefinition("ServerRetention", DisableParallelization = true)]
public sealed class RetentionCollection { }

public sealed class RetentionPostgresFactAttribute : FactAttribute
{
    public RetentionPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RETENTION_TEST_POSTGRES")))
            Skip = "Set RETENTION_TEST_POSTGRES to the documented disposable loopback database.";
    }
}

// No ambient application configuration, credentials or migrations/startup code is loaded.
internal sealed class RetentionDatabase : IAsyncDisposable
{
    private readonly string schema = "retention_" + Guid.NewGuid().ToString("N");
    private string admin = "";
    public string ConnectionString { get; private set; } = "";
    // Fixture-owned provider avoids the existing InMemory fixtures' process-global EF cache
    // pressure without disabling or suppressing ManyServiceProvidersCreatedWarning.
    private readonly ServiceProvider efServices = new ServiceCollection().AddEntityFrameworkNpgsql().BuildServiceProvider();
    public NinjaBotEntities Context() => new(new DbContextOptionsBuilder<NinjaBotEntities>()
        .UseNpgsql(ConnectionString).UseInternalServiceProvider(efServices).Options);
    public async Task InitializeAsync()
    {
        var b = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("RETENTION_TEST_POSTGRES"));
        if (b.Host is not ("localhost" or "127.0.0.1") || b.Database != "retention_test" || b.Username != "retention_test")
            throw new InvalidOperationException("Only the isolated loopback retention_test database/user is allowed.");
        admin = b.ConnectionString;
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using (var identity = new NpgsqlCommand("SELECT current_database() = 'retention_test' AND current_user = 'retention_test'", connection))
            Assert.Equal(true, await identity.ExecuteScalarAsync());
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection)) await create.ExecuteNonQueryAsync();
        b.SearchPath = schema;
        b.ApplicationName = schema;
        ConnectionString = b.ConnectionString;
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());
    }
    public async Task Sql(string sql)
    {
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync(sql);
    }
    public async Task<long> Count(string table)
    {
        await using var c = new NpgsqlConnection(ConnectionString); await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
    public async Task WaitForAdvisoryWaiter()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await using var c = new NpgsqlConnection(ConnectionString); await c.OpenAsync(timeout.Token);
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name=@app AND l.locktype='advisory' AND NOT l.granted)", c);
        cmd.Parameters.AddWithValue("app", schema);
        while (!(bool)(await cmd.ExecuteScalarAsync(timeout.Token))!) await Task.Delay(10, timeout.Token);
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (admin.Length == 0) return;
            await using var c = new NpgsqlConnection(admin); await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", c);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { await efServices.DisposeAsync(); }
    }
}
