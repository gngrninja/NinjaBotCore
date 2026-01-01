using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NinjaBotCore.Database;

var availableTables = TableCopyTasks.All.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
var options = MigrationOptions.Parse(args, availableTables.Keys);

if (string.IsNullOrWhiteSpace(options.PostgresConnectionString))
{
    Console.Error.WriteLine("A Postgres connection string is required. Pass --postgres \"Host=...\" or set DATAMIG_POSTGRES.");
    return 1;
}

var sqlitePath = Path.GetFullPath(options.SqlitePath);
if (!File.Exists(sqlitePath))
{
    Console.Error.WriteLine($"SQLite database not found at {sqlitePath}");
    return 1;
}

Console.WriteLine($"Using SQLite source: {sqlitePath}");
Console.WriteLine($"Using Postgres destination: {options.PostgresConnectionString}");

var sqliteOptions = new DbContextOptionsBuilder<NinjaBotEntities>()
    .UseSqlite($"Data Source={sqlitePath}")
    .Options;

var postgresOptions = new DbContextOptionsBuilder<NinjaBotEntities>()
    .UseNpgsql(options.PostgresConnectionString)
    .Options;

await using var sqliteContext = new NinjaBotEntities(sqliteOptions);
await using var postgresContext = new NinjaBotEntities(postgresOptions);
postgresContext.ChangeTracker.AutoDetectChangesEnabled = false;

Console.WriteLine("Normalizing source data...");
await SqliteDataNormalizer.NormalizeAsync(sqliteContext);

if (options.TruncateDestination)
{
    var tableList = string.Join(", ", options.Tables.Select(QuoteIdentifier));
    Console.WriteLine($"Truncating destination tables: {tableList}");
#pragma warning disable EF1002 // We build tableList from validated identifiers
    await postgresContext.Database.ExecuteSqlRawAsync($"TRUNCATE TABLE {tableList} RESTART IDENTITY CASCADE;");
#pragma warning restore EF1002
}

var tasksToRun = options.Tables.Select(name => availableTables[name]).ToList();
var includeChoices = tasksToRun.Any(t => t.Name == nameof(NinjaBotEntities.TriviaQuestionChoices));
if (includeChoices)
{
    Console.WriteLine("Copying TriviaQuestionChoices (custom pipeline)...");
    var migrated = await TriviaQuestionChoiceMigrator.CopyAsync(sqliteContext, postgresContext, options.BatchSize, CancellationToken.None);
    Console.WriteLine($"  Copied {migrated} TriviaQuestionChoices rows.");
    tasksToRun = tasksToRun.Where(t => t.Name != nameof(NinjaBotEntities.TriviaQuestionChoices)).ToList();
}

foreach (var task in tasksToRun)
{
    Console.WriteLine($"Copying {task.Name}...");
    var copied = await task.CopyAsync(sqliteContext, postgresContext, options.BatchSize, CancellationToken.None);
    Console.WriteLine($"  Copied {copied} rows.");
}

Console.WriteLine("Resetting PostgreSQL sequences...");
await ResetPostgresSequencesAsync(postgresContext);

Console.WriteLine("Migration complete.");
return 0;

static string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

static async Task ResetPostgresSequencesAsync(NinjaBotEntities context)
{
    Console.WriteLine("  Resetting all PostgreSQL sequences...");

    // Reset all sequences using pg_get_serial_sequence to find them automatically
    var resetSql = """
DO $$
DECLARE r record;
BEGIN
    FOR r IN
        SELECT pg_get_serial_sequence(format('%I.%I', tn.nspname, t.relname), a.attname) AS seq_name,
               format('%I.%I', tn.nspname, t.relname) AS tbl_name,
               a.attname AS col_name
        FROM pg_class t
        JOIN pg_namespace tn ON tn.oid = t.relnamespace
        JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum > 0
        WHERE pg_get_serial_sequence(format('%I.%I', tn.nspname, t.relname), a.attname) IS NOT NULL
    LOOP
        EXECUTE format(
            'SELECT setval(%L, COALESCE((SELECT MAX(%I) FROM %s),0)+1, false)',
            r.seq_name, r.col_name, r.tbl_name
        );
        RAISE NOTICE 'Reset sequence for table % column %', r.tbl_name, r.col_name;
    END LOOP;
END $$;
""";

    await context.Database.ExecuteSqlRawAsync(resetSql);
    Console.WriteLine("  Successfully reset all sequences");
}

sealed record MigrationOptions(
    string SqlitePath,
    string PostgresConnectionString,
    bool TruncateDestination,
    int BatchSize,
    IReadOnlyList<string> Tables)
{
    public static MigrationOptions Parse(string[] args, IEnumerable<string> availableTables)
    {
        var sqlitePath = Environment.GetEnvironmentVariable("DATAMIG_SQLITE") ?? "src/ninjabot.db";
        var postgres = Environment.GetEnvironmentVariable("DATAMIG_POSTGRES") ?? string.Empty;
        var truncate = false;
        var batchSize = 1000;
        var selectedTables = availableTables.ToList();

        var argQueue = new Queue<string>(args ?? Array.Empty<string>());
        while (argQueue.Count > 0)
        {
            var current = argQueue.Dequeue();
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var (key, value) = SplitArg(current, argQueue);
            switch (key.ToLowerInvariant())
            {
                case "sqlite":
                    sqlitePath = value ?? throw new ArgumentException("--sqlite requires a value.");
                    break;
                case "postgres":
                    postgres = value ?? throw new ArgumentException("--postgres requires a value.");
                    break;
                case "truncate":
                    truncate = true;
                    break;
                case "batch":
                case "batchsize":
                    if (value == null || !int.TryParse(value, out batchSize) || batchSize <= 0)
                    {
                        throw new ArgumentException("--batchSize must be a positive integer.");
                    }
                    break;
                case "tables":
                    if (value == null)
                    {
                        throw new ArgumentException("--tables requires a comma-separated list.");
                    }
                    selectedTables = value
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .ToList();
                    break;
            }
        }

        var availableSet = new HashSet<string>(availableTables, StringComparer.OrdinalIgnoreCase);
        foreach (var table in selectedTables)
        {
            if (!availableSet.Contains(table))
            {
                throw new ArgumentException($"Table '{table}' is not recognized. Valid options: {string.Join(", ", availableSet)}");
            }
        }

        return new MigrationOptions(sqlitePath, postgres, truncate, batchSize, selectedTables);
    }

    private static (string Key, string? Value) SplitArg(string current, Queue<string> remaining)
    {
        var trimmed = current.TrimStart('-');
        var equalsIndex = trimmed.IndexOf('=');
        if (equalsIndex >= 0)
        {
            var key = trimmed[..equalsIndex];
            var value = trimmed[(equalsIndex + 1)..];
            return (key, value);
        }

        if (remaining.Count > 0 && !remaining.Peek().StartsWith("--", StringComparison.Ordinal))
        {
            return (trimmed, remaining.Dequeue());
        }

        return (trimmed, null);
    }
}

interface ITableCopyTask
{
    string Name { get; }
    Task<long> CopyAsync(NinjaBotEntities source, NinjaBotEntities destination, int batchSize, CancellationToken cancellationToken);
}

sealed class TableCopyTask<T> : ITableCopyTask where T : class
{
    private readonly Func<NinjaBotEntities, DbSet<T>> _selector;
    public string Name { get; }

    public TableCopyTask(string name, Func<NinjaBotEntities, DbSet<T>> selector)
    {
        Name = name;
        _selector = selector;
    }

    public async Task<long> CopyAsync(NinjaBotEntities source, NinjaBotEntities destination, int batchSize, CancellationToken cancellationToken)
    {
        var srcSet = _selector(source);
        var destSet = _selector(destination);
        var buffer = new List<T>(batchSize);
        long copied = 0;

        await foreach (var entity in srcSet.AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            DateTimeKindNormalizer.Normalize(entity);
            buffer.Add(entity);
            if (buffer.Count >= batchSize)
            {
                await destSet.AddRangeAsync(buffer, cancellationToken);
                await destination.SaveChangesAsync(cancellationToken);
                destination.ChangeTracker.Clear();
                copied += buffer.Count;
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            await destSet.AddRangeAsync(buffer, cancellationToken);
            await destination.SaveChangesAsync(cancellationToken);
            destination.ChangeTracker.Clear();
            copied += buffer.Count;
        }

        return copied;
    }

}

static class TableCopyTasks
{
    public static IReadOnlyList<ITableCopyTask> All { get; } = new ITableCopyTask[]
    {
        new TableCopyTask<AchCategory>(nameof(NinjaBotEntities.AchCategories), ctx => ctx.AchCategories),
        new TableCopyTask<AuctionItemMapping>(nameof(NinjaBotEntities.AuctionItemMappings), ctx => ctx.AuctionItemMappings),
        new TableCopyTask<AwaySystem>(nameof(NinjaBotEntities.AwaySystem), ctx => ctx.AwaySystem),
        new TableCopyTask<Blacklist>(nameof(NinjaBotEntities.Blacklist), ctx => ctx.Blacklist),
        new TableCopyTask<C8Ball>(nameof(NinjaBotEntities.C8Ball), ctx => ctx.C8Ball),
        new TableCopyTask<ChannelOutput>(nameof(NinjaBotEntities.ChannelOutputs), ctx => ctx.ChannelOutputs),
        new TableCopyTask<CharStats>(nameof(NinjaBotEntities.CharStats), ctx => ctx.CharStats),
        new TableCopyTask<CurrentRaidTier>(nameof(NinjaBotEntities.CurrentRaidTier), ctx => ctx.CurrentRaidTier),
        new TableCopyTask<DiscordServer>(nameof(NinjaBotEntities.DiscordServers), ctx => ctx.DiscordServers),
        new TableCopyTask<FindWowCheeve>(nameof(NinjaBotEntities.FindWowCheeves), ctx => ctx.FindWowCheeves),
        new TableCopyTask<Giphy>(nameof(NinjaBotEntities.Giphy), ctx => ctx.Giphy),
        new TableCopyTask<LogMonitoring>(nameof(NinjaBotEntities.LogMonitoring), ctx => ctx.LogMonitoring),
        new TableCopyTask<Note>(nameof(NinjaBotEntities.Notes), ctx => ctx.Notes),
        new TableCopyTask<QuestionAnswer>(nameof(NinjaBotEntities.QuestionAnswers), ctx => ctx.QuestionAnswers),
        new TableCopyTask<Request>(nameof(NinjaBotEntities.Requests), ctx => ctx.Requests),
        new TableCopyTask<RlStat>(nameof(NinjaBotEntities.RlStats), ctx => ctx.RlStats),
        new TableCopyTask<RlUserStat>(nameof(NinjaBotEntities.RlUserStats), ctx => ctx.RlUserStats),
        new TableCopyTask<ServerGreeting>(nameof(NinjaBotEntities.ServerGreetings), ctx => ctx.ServerGreetings),
        new TableCopyTask<ServerSetting>(nameof(NinjaBotEntities.ServerSettings), ctx => ctx.ServerSettings),
        new TableCopyTask<TriviaCategory>(nameof(NinjaBotEntities.TriviaCategories), ctx => ctx.TriviaCategories),
        new TableCopyTask<TriviaQuestion>(nameof(NinjaBotEntities.TriviaQuestion), ctx => ctx.TriviaQuestion),
        new TableCopyTask<TriviaQuestionChoice>(nameof(NinjaBotEntities.TriviaQuestionChoices), ctx => ctx.TriviaQuestionChoices),
        new TableCopyTask<Warnings>(nameof(NinjaBotEntities.Warnings), ctx => ctx.Warnings),
        new TableCopyTask<WordList>(nameof(NinjaBotEntities.WordList), ctx => ctx.WordList),
        new TableCopyTask<WowAuctionPrice>(nameof(NinjaBotEntities.WowAuctionPrices), ctx => ctx.WowAuctionPrices),
        new TableCopyTask<WowAuctions>(nameof(NinjaBotEntities.WowAuctions), ctx => ctx.WowAuctions),
        new TableCopyTask<WowResources>(nameof(NinjaBotEntities.WowResources), ctx => ctx.WowResources),
        new TableCopyTask<WowGuildAssociations>(nameof(NinjaBotEntities.WowGuildAssociations), ctx => ctx.WowGuildAssociations),
        new TableCopyTask<WowClassicGuild>(nameof(NinjaBotEntities.WowClassicGuild), ctx => ctx.WowClassicGuild),
        new TableCopyTask<WowVanillaGuild>(nameof(NinjaBotEntities.WowVanillaGuild), ctx => ctx.WowVanillaGuild),
        new TableCopyTask<WowCharAssociation>(nameof(NinjaBotEntities.WowCharAssociation), ctx => ctx.WowCharAssociation),
        new TableCopyTask<WowMChar>(nameof(NinjaBotEntities.WowMChar), ctx => ctx.WowMChar),
        new TableCopyTask<WclPosted>(nameof(NinjaBotEntities.WclPosted), ctx => ctx.WclPosted)
    };
}

static class DateTimeKindNormalizer
{
    private static readonly ConcurrentDictionary<Type, List<PropertyInfo>> Cache = new();

    public static void Normalize(object entity)
    {
        if (entity == null)
        {
            return;
        }

        var type = entity.GetType();
        var props = Cache.GetOrAdd(type, t =>
        {
            return t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanRead && p.CanWrite && (p.PropertyType == typeof(DateTime) || p.PropertyType == typeof(DateTime?)))
                .ToList();
        });

        foreach (var prop in props)
        {
            var value = prop.GetValue(entity);
            if (value is DateTime)
            {
                var dt = (DateTime)value;
                if (dt.Kind != DateTimeKind.Utc)
                {
                    prop.SetValue(entity, DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                }
                continue;
            }

            if (value is DateTime?)
            {
                var nullable = (DateTime?)value;
                if (nullable.HasValue && nullable.Value.Kind != DateTimeKind.Utc)
                {
                    prop.SetValue(entity, DateTime.SpecifyKind(nullable.Value, DateTimeKind.Utc));
                }
            }
        }
    }
}

static class SqliteDataNormalizer
{
    private static readonly (string Table, string Column)[] NullableDateTimeColumns =
    {
        ("AwaySystem", "TimeAway"),
        ("WowVanillaGuild", "TimeSet"),
        ("WowGuildAssociations", "TimeSet"),
        ("WowClassicGuild", "TimeSet"),
        ("WowCharAssociation", "TimeSet"),
        ("WowAuctions", "DateModified"),
        ("ServerGreetings", "TimeSet"),
        ("Notes", "TimeSet"),
        ("Requests", "RequestTime"),
        ("QuestionAnswers", "AnswerTime"),
        ("LogMonitoring", "LatestLogVanilla"),
        ("LogMonitoring", "LatestLogRetail"),
        ("LogMonitoring", "LatestLogClassic"),
        ("LogMonitoring", "LatestLog"),
        ("ChannelOutputs", "SetTime"),
        ("Blacklist", "WhenBlacklisted"),
        ("Warnings", "TimeIssued")
    };

    private static readonly (string Table, string Column, string DefaultValue)[] NonNullableDateTimeColumns =
    {
        ("CharStats", "LastModified", "1970-01-01T00:00:00")
    };

    private static readonly (string Table, string Column)[] ZeroByteStringColumns =
    {
        ("Giphy", "ServerName"),
        ("Requests", "Command"),
        ("Requests", "Parameters"),
        ("Requests", "FailureReason"),
        ("Notes", "Note1")
    };

    public static async Task NormalizeAsync(NinjaBotEntities context)
    {
        foreach (var (table, column) in NullableDateTimeColumns)
        {
            var sql = $$"""
UPDATE "{{table}}" SET "{{column}}" = NULL
WHERE TRIM(COALESCE("{{column}}", '')) IN ('', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0000-00-00 00:00:00');
""";
            await context.Database.ExecuteSqlRawAsync(sql);
        }

        foreach (var (table, column, defaultValue) in NonNullableDateTimeColumns)
        {
            var sql = $$"""
UPDATE "{{table}}" SET "{{column}}" = '{{defaultValue}}'
WHERE TRIM(COALESCE("{{column}}", '')) IN ('', '0', '0000-00-00 00:00:00');
""";
            await context.Database.ExecuteSqlRawAsync(sql);
        }

        foreach (var (table, column) in ZeroByteStringColumns)
        {
            var sql = $$"""
UPDATE "{{table}}" SET "{{column}}" = REPLACE(COALESCE("{{column}}", ''), char(0), '')
WHERE INSTR(COALESCE("{{column}}", ''), char(0)) > 0;
""";
            await context.Database.ExecuteSqlRawAsync(sql);
        }
    }
}

static class TriviaQuestionChoiceMigrator
{
    public static async Task<long> CopyAsync(NinjaBotEntities sqliteContext, NinjaBotEntities postgresContext, int batchSize, CancellationToken cancellationToken)
    {
        await sqliteContext.Database.OpenConnectionAsync(cancellationToken);
        await using var sqliteCmd = sqliteContext.Database.GetDbConnection().CreateCommand();
        sqliteCmd.CommandText = "SELECT ChoiceId, QuestionId, Choice, IsRightChoice FROM TriviaQuestionChoices";
        await using var reader = await sqliteCmd.ExecuteReaderAsync(cancellationToken);

        var buffer = new List<TriviaQuestionChoice>(batchSize);
        long copied = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var entity = new TriviaQuestionChoice
            {
                ChoiceId = reader.GetInt64(0),
                QuestionId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1),
                Choice = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsRightChoice = reader.IsDBNull(3) ? (bool?)null : reader.GetInt64(3) != 0
            };
            DateTimeKindNormalizer.Normalize(entity);
            buffer.Add(entity);
            if (buffer.Count >= batchSize)
            {
                await FlushAsync(buffer, postgresContext, cancellationToken);
                copied += buffer.Count;
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            await FlushAsync(buffer, postgresContext, cancellationToken);
            copied += buffer.Count;
        }

        return copied;
    }

    private static async Task FlushAsync(List<TriviaQuestionChoice> buffer, NinjaBotEntities destination, CancellationToken cancellationToken)
    {
        await destination.TriviaQuestionChoices.AddRangeAsync(buffer, cancellationToken);
        await destination.SaveChangesAsync(cancellationToken);
        destination.ChangeTracker.Clear();
    }
}
