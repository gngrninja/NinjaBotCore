using System;
using System.Linq;
using Discord;
using DbPoll = NinjaBotCore.Database.Poll;

namespace NinjaBotCore.Services.Api
{
    internal static class PollHelpers
    {
        /// <summary>
        /// Builds an embed showing closed poll results with progress bars.
        /// </summary>
        internal static EmbedBuilder BuildClosedPollEmbed(DbPoll poll, int totalVotes)
        {
            var embed = new EmbedBuilder()
                .WithTitle($"📊 {poll.Question}")
                .WithColor(Color.Red) // Red for closed
                .WithFooter($"Created by {poll.CreatedByName} • {totalVotes} total votes • Closed")
                .WithTimestamp(poll.CreatedAt);

            // Add options with vote counts and progress bars
            foreach (var option in poll.PollOptions.OrderBy(o => o.DisplayOrder))
            {
                var optionVotes = poll.PollVotes.Count(v => v.OptionId == option.Id);
                var percentage = totalVotes > 0 ? (double)optionVotes / totalVotes * 100 : 0;
                var barLength = 10;
                var filledBars = (int)Math.Round(percentage / 100 * barLength);
                var progressBar = new string('█', filledBars) + new string('░', barLength - filledBars);

                var votersList = "";
                if (!poll.IsAnonymous && optionVotes > 0)
                {
                    var voters = poll.PollVotes
                        .Where(v => v.OptionId == option.Id)
                        .Select(v => v.UserName)
                        .Take(5);
                    votersList = $"\n*{string.Join(", ", voters)}*";
                    if (optionVotes > 5)
                        votersList += $" *+{optionVotes - 5} more*";
                }

                var emote = !string.IsNullOrEmpty(option.Emote) ? option.Emote + " " : "";
                embed.AddField(
                    $"{emote}{option.OptionText}",
                    $"{progressBar} {percentage:F1}% ({optionVotes} votes){votersList}",
                    inline: false
                );
            }

            return embed;
        }

        /// <summary>
        /// Parses a duration string like "1h", "24h", "1d", "7d", "1w" into a DateTime.
        /// </summary>
        internal static DateTime? ParsePollDuration(string duration)
        {
            if (string.IsNullOrWhiteSpace(duration))
                return null;

            duration = duration.Trim().ToLowerInvariant();

            // Try to parse number + unit
            if (duration.Length < 2)
                return null;

            var unit = duration[^1];
            if (!int.TryParse(duration[..^1], out var value) || value <= 0)
                return null;

            return unit switch
            {
                'h' => DateTime.UtcNow.AddHours(value),
                'd' => DateTime.UtcNow.AddDays(value),
                'w' => DateTime.UtcNow.AddDays(value * 7),
                _ => null
            };
        }
    }
}
