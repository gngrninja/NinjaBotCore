#nullable enable

using System.Collections.Generic;
using System.Linq;
using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Services.DeliveryProblems
{
    /// <summary>
    /// The words on the Actionable Alerts page and in the DM sent to a server. Server and
    /// channel names are typed by users, so they are escaped before they reach Discord.
    /// </summary>
    public static class DeliveryProblemText
    {
        public const string Setter = "setter";
        public const string Owner = "owner";

        /// <summary>A setter found through the WoW guild link, for features that don't record who set them up.</summary>
        public const string GuildLinker = "guild_link";

        private static readonly ChannelPermission[] PostPermissions =
            { ChannelPermission.ViewChannel, ChannelPermission.SendMessages, ChannelPermission.EmbedLinks };

        private static readonly ChannelPermission[] FilterPermissions =
            { ChannelPermission.ViewChannel, ChannelPermission.ManageMessages };

        public static IReadOnlyList<ChannelPermission> Needed(string feature) =>
            feature == DeliveryFeature.WordFilter ? FilterPermissions : PostPermissions;

        public static bool CanTurnOff(string feature) => feature != DeliveryFeature.WordFilter;

        public static string Feature(string feature) => feature switch
        {
            DeliveryFeature.NewLog => "New log posts",
            DeliveryFeature.Welcome => "Welcome messages",
            DeliveryFeature.Leave => "Leave messages",
            DeliveryFeature.ModerationLog => "Moderation log",
            DeliveryFeature.RealmAlert => "Realm status alerts",
            DeliveryFeature.WordFilter => "Word filter",
            _ => feature
        };

        /// <summary>A short line for the admin page. Plain text; the page escapes it.</summary>
        public static string Problem(string problem, string? channelName, int? discordCode)
        {
            var channel = channelName == null ? "the channel" : "#" + channelName;
            return problem switch
            {
                DeliveryProblemKind.ChannelMissing => channelName == null ? "Channel deleted" : $"{channel} was deleted",
                DeliveryProblemKind.NoAccess => $"NinjaBot can't see {channel}",
                DeliveryProblemKind.MissingPermissions => $"NinjaBot is missing permissions in {channel}",
                _ => discordCode == null ? "Discord refused the post" : $"Discord error {discordCode}"
            };
        }

        /// <summary>The DM for the person who set the feature up, or the server owner.</summary>
        public static string Dm(
            string feature,
            string problem,
            string? serverName,
            ulong channelId,
            string? channelName,
            IReadOnlyList<string> missing,
            string recipient)
        {
            var server = RaidRecapRules.Text(serverName ?? "your server", 80);
            var here = channelId > 0 && problem != DeliveryProblemKind.ChannelMissing
                ? $"<#{channelId}>"
                : channelName != null ? "#" + RaidRecapRules.Text(channelName, 60) : "its channel";
            var needed = missing.Count > 0 ? missing : Needed(feature).Select(DeliveryProblemDiscord.Name).ToArray();
            var permissions = Join(needed);
            var wordFilter = feature == DeliveryFeature.WordFilter;

            var lines = new List<string>
            {
                wordFilter
                    ? $"**NinjaBot can't remove blocked words in {server}**"
                    : $"**NinjaBot can't post {What(feature)} in {server}**"
            };

            lines.Add(problem switch
            {
                DeliveryProblemKind.ChannelMissing =>
                    $"I'm set up to post {What(feature)} in {here}, but that channel no longer exists.",
                DeliveryProblemKind.NoAccess when wordFilter =>
                    $"The word filter is on, but I can't see {here}, so I can't remove blocked words there.",
                DeliveryProblemKind.NoAccess =>
                    $"I'm set up to post {What(feature)} in {here}, but I can't see that channel.",
                _ when wordFilter =>
                    $"The word filter is on, but I'm missing {permissions} in {here}, so I can't remove blocked words there.",
                DeliveryProblemKind.MissingPermissions =>
                    $"I'm set up to post {What(feature)} in {here}, but I'm missing {permissions} there.",
                _ => $"I'm set up to post {What(feature)} in {here}, but Discord is refusing my posts there."
            });

            if (wordFilter)
            {
                lines.Add($"**To fix it:** give NinjaBot {permissions} in {here}. To stop filtering, remove the words with `/remove-word`.");
            }
            else if (problem == DeliveryProblemKind.ChannelMissing)
            {
                lines.Add("**To fix it:** " + Move(feature));
                lines.Add(Stop(feature));
            }
            else
            {
                lines.Add($"**To fix it:** give NinjaBot {permissions} in {here}.");
                lines.Add("Or " + LowerFirst(Move(feature)) + " " + Stop(feature));
            }

            lines.Add(recipient switch
            {
                Owner => $"-# You're getting this because you own {server}.",
                GuildLinker => $"-# You're getting this because you linked {server}'s WoW guild to NinjaBot.",
                _ => $"-# You're getting this because you set this up in {server}."
            });
            return string.Join("\n", lines);
        }

        private static string What(string feature) => feature switch
        {
            DeliveryFeature.NewLog => "new Warcraft Logs reports",
            DeliveryFeature.Welcome => "welcome messages",
            DeliveryFeature.Leave => "leave messages",
            DeliveryFeature.ModerationLog => "the moderation log",
            DeliveryFeature.RealmAlert => "realm status alerts",
            _ => "messages"
        };

        // The commands as they work today: /watchlogs and /toggle-greetings turn off on the first
        // run and on again, in the channel they're run in, on the second.
        private static string Move(string feature) => feature switch
        {
            DeliveryFeature.NewLog => "To use another channel, run `/watchlogs` there twice: the first run turns posting off, the second turns it on in that channel.",
            DeliveryFeature.Welcome => "To use another channel, run `/toggle-greetings` there twice: the first run turns them off, the second turns them on in that channel.",
            DeliveryFeature.Leave => "To use another channel, run `/set-parting-channel` there.",
            DeliveryFeature.ModerationLog => "To use another channel, run `/watch action:Enable` there for each type you want logged.",
            DeliveryFeature.RealmAlert => "To use another channel, remove each realm with `/realm-watch remove`, then add it again with `/realm-watch add` and pick the new `channel` (needs Administrator).",
            _ => "To use another channel, set the feature up again there."
        };

        private static string Stop(string feature) => feature switch
        {
            DeliveryFeature.NewLog => "To stop them, run `/watchlogs` once.",
            DeliveryFeature.Welcome => "To stop them, run `/toggle-greetings` once.",
            DeliveryFeature.Leave => "To stop them, run `/toggle-partings`.",
            DeliveryFeature.ModerationLog => "To stop it, run `/watch action:Disable` for each type.",
            DeliveryFeature.RealmAlert => "To stop them, remove each realm with `/realm-watch remove`.",
            _ => ""
        };

        private static string Join(IReadOnlyList<string> items) => items.Count switch
        {
            0 => "",
            1 => items[0],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1]
        };

        private static string LowerFirst(string text) =>
            text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
    }
}
