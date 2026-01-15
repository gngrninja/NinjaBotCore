using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Database;
using DbPoll = NinjaBotCore.Database.Poll;
using DbPollOption = NinjaBotCore.Database.PollOption;
using DbPollVote = NinjaBotCore.Database.PollVote;

namespace NinjaBotCore.Modules.Interactions.Polls
{
    /// <summary>
    /// Builds rich embed messages for poll results.
    /// </summary>
    public class PollResultsBuilder
    {
        /// <summary>
        /// Builds a results embed for a closed poll.
        /// </summary>
        public Embed BuildResultsEmbed(DbPoll poll, List<DbPollOption> options, List<DbPollVote> votes, string? closedBy = null, bool wasExpired = false)
        {
            var totalVotes = votes.Count;
            var voteCounts = options.ToDictionary(o => o.Id, o => votes.Count(v => v.OptionId == o.Id));
            var maxVotes = voteCounts.Values.DefaultIfEmpty(0).Max();
            var winners = options.Where(o => voteCounts.GetValueOrDefault(o.Id, 0) == maxVotes && maxVotes > 0).ToList();

            var embed = new EmbedBuilder()
                .WithTitle("📊 Poll Results")
                .WithColor(new Color(255, 215, 0)) // Gold color for results
                .WithTimestamp(DateTimeOffset.UtcNow);

            // Add the question as description with formatting
            var description = new StringBuilder();
            description.AppendLine($"**{poll.Question}**");
            description.AppendLine();

            embed.WithDescription(description.ToString());

            // Add fields for each option with vote counts
            foreach (var option in options.OrderBy(o => o.DisplayOrder))
            {
                var optionVotes = voteCounts.GetValueOrDefault(option.Id, 0);
                var percentage = totalVotes > 0 ? (optionVotes * 100.0 / totalVotes) : 0;
                var barLength = (int)(percentage / 5); // 20 chars max
                var bar = new string('█', Math.Min(barLength, 20)) + new string('░', Math.Max(20 - barLength, 0));

                // Determine if this is a winner
                var isWinner = winners.Contains(option) && maxVotes > 0;
                var emote = isWinner ? "🏆 " : (!string.IsNullOrEmpty(option.Emote) ? option.Emote + " " : "");

                embed.AddField($"{emote}{option.OptionText}",
                    $"`{bar}` {percentage:F1}% ({optionVotes} vote{(optionVotes != 1 ? "s" : "")})",
                    inline: false);
            }

            // Add jump link to original poll
            if (poll.GuildId != 0 && poll.ChannelId != 0 && poll.MessageId != 0)
            {
                embed.AddField("📎 Original Poll",
                    $"[Jump to Poll](https://discord.com/channels/{poll.GuildId}/{poll.ChannelId}/{poll.MessageId})",
                    inline: true);
            }

            // Build footer with stats
            var footerParts = new List<string>
            {
                $"{totalVotes} total vote{(totalVotes != 1 ? "s" : "")}"
            };

            // Calculate duration
            if (poll.CreatedAt != default)
            {
                var closedTime = poll.ClosedAt ?? DateTime.UtcNow;
                var duration = closedTime - poll.CreatedAt;
                footerParts.Add(FormatDuration(duration));
            }

            // Add how it was closed
            if (wasExpired)
            {
                footerParts.Add("Auto-expired");
            }
            else if (!string.IsNullOrEmpty(closedBy))
            {
                footerParts.Add($"Closed by {closedBy}");
            }

            embed.WithFooter(string.Join(" • ", footerParts));

            return embed.Build();
        }

        /// <summary>
        /// Builds a string of voter mentions for the results message.
        /// </summary>
        public string BuildVoterMentions(List<DbPollVote> votes, bool isAnonymous, int maxMentions = 10)
        {
            if (isAnonymous || votes.Count == 0)
                return string.Empty;

            // Get unique user IDs
            var uniqueUserIds = votes.Select(v => v.UserId).Distinct().ToList();

            if (uniqueUserIds.Count == 0)
                return string.Empty;

            var mentions = uniqueUserIds
                .Take(maxMentions)
                .Select(id => $"<@{id}>")
                .ToList();

            var result = new StringBuilder("**Voters:** ");
            result.Append(string.Join(" ", mentions));

            if (uniqueUserIds.Count > maxMentions)
            {
                result.Append($" (+{uniqueUserIds.Count - maxMentions} more)");
            }

            return result.ToString();
        }

        /// <summary>
        /// Formats a duration into a human-readable string.
        /// </summary>
        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalDays >= 1)
            {
                var days = (int)duration.TotalDays;
                return $"Open for {days} day{(days != 1 ? "s" : "")}";
            }
            else if (duration.TotalHours >= 1)
            {
                var hours = (int)duration.TotalHours;
                return $"Open for {hours} hour{(hours != 1 ? "s" : "")}";
            }
            else
            {
                var minutes = (int)duration.TotalMinutes;
                return $"Open for {minutes} minute{(minutes != 1 ? "s" : "")}";
            }
        }
    }
}
