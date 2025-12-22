using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Modules.Wow;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    public class WarcraftLogsV2Interact : InteractionModuleBase<ShardedInteractionContext>
    {
        private readonly WarcraftLogsV2Client _v2Client;
        private readonly ILogger<WarcraftLogsV2Interact> _logger;

        public WarcraftLogsV2Interact(WarcraftLogsV2Client v2Client, ILogger<WarcraftLogsV2Interact> logger)
        {
            _v2Client = v2Client;
            _logger = logger;
        }

        [SlashCommand("test-wclv2", "Test WarcraftLogs v2 API")]
        public async Task TestWclV2Async(
            [Summary("guild", "Guild name")] string guildName = "Limit",
            [Summary("server", "Server slug (lowercase-with-hyphens)")] string serverSlug = "illidan",
            [Summary("region", "Region (us/eu)")] string region = "us")
        {
            await DeferAsync(ephemeral: true);

            try
            {
                _logger.LogInformation($"Testing v2 API for {guildName}-{serverSlug}-{region}");

                var reports = await _v2Client.GetGuildReportsAsync(guildName, serverSlug, region, limit: 5);

                var embed = new EmbedBuilder();
                embed.WithTitle($"WarcraftLogs v2 API Test");
                embed.WithColor(Color.Blue);
                embed.AddField("Guild", $"{guildName}-{serverSlug} ({region.ToUpper()})", inline: false);
                embed.AddField("Reports Found", reports.Count.ToString(), inline: true);
                embed.AddField("API Version", "v2 (GraphQL + OAuth)", inline: true);

                if (reports.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var report in reports.Take(3))
                    {
                        sb.AppendLine($"**[{report.Title}]({report.ReportURL})**");
                        sb.AppendLine($"└─ By {report.OwnerName} • {report.ZoneName}");
                        sb.AppendLine($"└─ {DateTimeOffset.FromUnixTimeMilliseconds(report.StartTime).ToString("MMM dd, yyyy")}");
                        sb.AppendLine();
                    }
                    embed.AddField("Latest Reports", sb.ToString(), inline: false);
                    embed.WithColor(Color.Green);
                }
                else
                {
                    embed.WithDescription("No reports found for this guild.");
                    embed.WithColor(Color.Orange);
                }

                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "v2 API test failed");

                var errorEmbed = new EmbedBuilder()
                    .WithTitle("v2 API Test Failed")
                    .WithDescription($"```\n{ex.Message}\n```")
                    .WithColor(Color.Red);

                await FollowupAsync(embed: errorEmbed.Build(), ephemeral: true);
            }
        }

        [SlashCommand("compare-wcl", "Compare WarcraftLogs v1 vs v2 API")]
        public async Task CompareWclAsync(
            [Summary("guild", "Guild name")] string guildName,
            [Summary("realm", "Realm name (with spaces)")] string realm,
            [Summary("server-slug", "Server slug (lowercase-with-hyphens)")] string serverSlug,
            [Summary("region", "Region (us/eu)")] string region = "us")
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var testUtil = Context.Guild != null
                    ? (WarcraftLogsV2Test)null
                    : null;

                // Try to get the test utility if available
                var services = Context.Interaction.GetType()
                    .GetProperty("ServiceProvider")?
                    .GetValue(Context.Interaction) as IServiceProvider;

                if (services != null)
                {
                    testUtil = services.GetService(typeof(WarcraftLogsV2Test)) as WarcraftLogsV2Test;
                }

                if (testUtil != null)
                {
                    var comparison = await testUtil.CompareV1AndV2Async(guildName, realm, serverSlug, region);

                    // Split into chunks if too long
                    var chunks = SplitString(comparison, 1990);

                    await FollowupAsync(text: $"```\n{chunks[0]}\n```", ephemeral: true);

                    for (int i = 1; i < chunks.Count; i++)
                    {
                        await FollowupAsync(text: $"```\n{chunks[i]}\n```", ephemeral: true);
                    }
                }
                else
                {
                    await FollowupAsync("WarcraftLogsV2Test not registered in DI. Add `.AddSingleton<WarcraftLogsV2Test>()` to NinjaBot.cs", ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Comparison test failed");
                await FollowupAsync($"❌ Test failed: {ex.Message}", ephemeral: true);
            }
        }

        private System.Collections.Generic.List<string> SplitString(string text, int maxLength)
        {
            var result = new System.Collections.Generic.List<string>();
            var lines = text.Split('\n');
            var current = new StringBuilder();

            foreach (var line in lines)
            {
                if (current.Length + line.Length + 1 > maxLength)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                current.AppendLine(line);
            }

            if (current.Length > 0)
            {
                result.Add(current.ToString());
            }

            return result;
        }
    }
}
