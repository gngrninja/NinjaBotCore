using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Wow
{
    /// <summary>
    /// Test class to compare WarcraftLogs v1 and v2 API responses
    /// </summary>
    public class WarcraftLogsV2Test
    {
        private readonly WarcraftLogs _v1Client;
        private readonly WarcraftLogsV2Client _v2Client;
        private readonly ILogger _logger;

        public WarcraftLogsV2Test(WarcraftLogs v1Client, WarcraftLogsV2Client v2Client, ILogger<WarcraftLogsV2Test> logger)
        {
            _v1Client = v1Client;
            _v2Client = v2Client;
            _logger = logger;
        }

        /// <summary>
        /// Compares v1 and v2 API responses for the same guild
        /// </summary>
        public async Task<string> CompareV1AndV2Async(string guildName, string realm, string realmSlug, string region)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine("WarcraftLogs v1 vs v2 API Comparison");
            sb.AppendLine("========================================");
            sb.AppendLine($"Guild: {guildName}");
            sb.AppendLine($"Realm: {realm} ({realmSlug})");
            sb.AppendLine($"Region: {region.ToUpper()}");
            sb.AppendLine();

            // Test v1 API
            sb.AppendLine("--- v1 API (REST) ---");
            try
            {
                var v1Start = DateTime.UtcNow;
                var v1Reports = await _v1Client.GetReportsFromGuild(guildName, realm, region, isList: true, flip: false);
                var v1Duration = (DateTime.UtcNow - v1Start).TotalMilliseconds;

                if (v1Reports != null && v1Reports.Count > 0)
                {
                    sb.AppendLine($"✓ Success! Retrieved {v1Reports.Count} reports in {v1Duration:F0}ms");
                    sb.AppendLine($"Latest Report:");
                    var latest = v1Reports[0];
                    sb.AppendLine($"  ID: {latest.id}");
                    sb.AppendLine($"  Title: {latest.title}");
                    sb.AppendLine($"  Owner: {latest.owner}");
                    sb.AppendLine($"  Zone: {latest.zoneName}");
                    sb.AppendLine($"  Start: {latest.start.UnixTimeStampToDateTime()}");
                    sb.AppendLine($"  URL: {latest.reportURL}");
                }
                else
                {
                    sb.AppendLine("✗ No reports found");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"✗ Error: {ex.Message}");
            }

            sb.AppendLine();

            // Test v2 API
            sb.AppendLine("--- v2 API (GraphQL + OAuth) ---");
            try
            {
                var v2Start = DateTime.UtcNow;
                var v2Reports = await _v2Client.GetGuildReportsAsync(guildName, realmSlug, region, limit: 5);
                var v2Duration = (DateTime.UtcNow - v2Start).TotalMilliseconds;

                if (v2Reports != null && v2Reports.Count > 0)
                {
                    sb.AppendLine($"✓ Success! Retrieved {v2Reports.Count} reports in {v2Duration:F0}ms");
                    sb.AppendLine($"Latest Report:");
                    var latest = v2Reports[0];
                    sb.AppendLine($"  ID: {latest.Code}");
                    sb.AppendLine($"  Title: {latest.Title}");
                    sb.AppendLine($"  Owner: {latest.OwnerName}");
                    sb.AppendLine($"  Zone: {latest.ZoneName}");
                    sb.AppendLine($"  Start: {DateTimeOffset.FromUnixTimeMilliseconds(latest.StartTime).UtcDateTime}");
                    sb.AppendLine($"  URL: {latest.ReportURL}");
                }
                else
                {
                    sb.AppendLine("✗ No reports found");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"✗ Error: {ex.Message}");
                _logger.LogError(ex, "v2 API test failed");
            }

            sb.AppendLine();
            sb.AppendLine("========================================");

            return sb.ToString();
        }

        /// <summary>
        /// Quick test method - just tests v2 API
        /// </summary>
        public async Task<string> TestV2OnlyAsync(string guildName, string realmSlug, string region)
        {
            return await _v2Client.TestGuildReportsAsync(guildName, realmSlug, region);
        }
    }
}
