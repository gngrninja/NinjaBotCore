using System.Text.Json;

namespace NinjaBotHelpers.Retention;

// Only code-owned categories, IDs, timestamps and aggregate counts cross this boundary.
// No exception objects, SQL, Discord bodies, names or row values belong in these DTOs.
public sealed class RetentionServerReport
{
    public int Version => 1;
    public string Event => "retention.server.finished";
    public required string SweepId { get; init; }
    public string OperationId { get; } = Guid.NewGuid().ToString("N");
    public required string Mode { get; init; }
    // Snowflakes are strings to avoid loss of precision in JSON consumers.
    public required string ServerId { get; init; }
    public DateTime DepartedUtc { get; init; }
    public DateTime? DueUtc { get; init; }
    public DateTime EligibilityAsOfUtc { get; init; }
    public double DurationMs { get; internal set; }
    public string Outcome { get; internal set; } = "stale_ineligible";
    public string Stage { get; internal set; } = "eligibility";
    public string DeletionState { get; internal set; } = "not_started";
    public string TransactionState { get; internal set; } = "not_started";
    public string? ErrorCategory { get; internal set; }
    public IReadOnlyDictionary<string, long>? CommittedRowsByTable { get; internal set; } = new Dictionary<string, long>();
    public long? CommittedDeletedRows => CommittedRowsByTable?.Values.Sum();
    public IReadOnlyDictionary<string, long> PlannedRowsByTable { get; internal set; } = new Dictionary<string, long>();
    public long PlannedRows => PlannedRowsByTable.Values.Sum();
}

internal static class RetentionReports
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static void Write<T>(ILogger logger, T report) =>
        logger.LogInformation("ServerRetentionReport {ReportJson}", JsonSerializer.Serialize(report, Json));

    public static string ErrorCategory(Exception exception) => exception switch
    {
        OperationCanceledException => "cancelled_or_timeout",
        Npgsql.NpgsqlException => "database",
        HttpRequestException => "network",
        _ => "internal"
    };
}
