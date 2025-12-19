// Quick schema verification script
// Run with: dotnet script tools/verify-schema.cs

using System;
using Npgsql;

var connectionString = Environment.GetEnvironmentVariable("NINJABOT_ConnectionStrings__NinjaBot")
    ?? "Host=localhost;Port=5432;Database=ninjabot;Username=ninjabot;Password=password";

Console.WriteLine("Checking CurrentRaidTier schema...\n");

using var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();

using var cmd = new NpgsqlCommand(@"
    SELECT
        column_name,
        data_type,
        udt_name
    FROM information_schema.columns
    WHERE table_name = 'CurrentRaidTier'
    ORDER BY ordinal_position", conn);

using var reader = await cmd.ExecuteReaderAsync();

Console.WriteLine($"{"Column",-20} {"Type",-15} {"Native Type",-15}");
Console.WriteLine(new string('-', 50));

while (await reader.ReadAsync())
{
    var column = reader.GetString(0);
    var dataType = reader.GetString(1);
    var udtName = reader.GetString(2);

    var status = "";
    if (column == "Id" || column == "WclZoneId")
    {
        status = udtName == "int8" ? " ✅" : " ❌ Should be bigint!";
    }

    Console.WriteLine($"{column,-20} {dataType,-15} {udtName,-15}{status}");
}

Console.WriteLine("\nExpected:");
Console.WriteLine("  Id: bigint (int8)");
Console.WriteLine("  WclZoneId: bigint (int8)");
