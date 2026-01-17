using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// WoW Guild-related commands for managing guild associations and viewing guild info.
    /// Includes: /ginfo, /setguild, /getguild
    /// </summary>
    public class GuildCommands : NinjaBotBaseModule
    {
        private readonly ILogger<GuildCommands> _logger;
        private readonly WowApi _wowApi;
        private readonly RaiderIOApi _rioApi;
        private readonly WowUtilities _wowUtils;
        private readonly WowStaticDataService _staticDataService;

        // Cooldown for guild association changes (10 minutes)
        private static readonly TimeSpan GuildChangeCooldown = TimeSpan.FromMinutes(10);

        public GuildCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<GuildCommands> logger,
            WowApi wowApi,
            RaiderIOApi rioApi,
            WowUtilities wowUtils,
            WowStaticDataService staticDataService)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowApi = wowApi;
            _rioApi = rioApi;
            _wowUtils = wowUtils;
            _staticDataService = staticDataService;
        }

        [SlashCommand("ginfo", "Get guild information")]
        public async Task GetRioGuildStats()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();
            var guildInfo = Context.Guild;

            string title = string.Empty;
            string discordGuildName = string.Empty;
            string thumbUrl = string.Empty;
            string region = string.Empty;

            if (guildInfo == null)
            {
                discordGuildName = Context.User.Username;
                thumbUrl = Context.User.GetAvatarUrl();
            }
            else
            {
                discordGuildName = Context.Guild.Name;
                thumbUrl = Context.Guild.IconUrl;
            }

            var guildObject = await _wowUtils.GetGuildName(Context);
            var guildStats = await _rioApi.GetRioGuildInfoAsync(guildName: guildObject.guildName, realmName: guildObject.realmSlug, region: guildObject.regionName);

            string normalKilled = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.ManaforgeOmega.NormalBossesKilled);
            string heroicKilled = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.ManaforgeOmega.HeroicBossesKilled);
            string mythicKilled = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.ManaforgeOmega.MythicBossesKilled);
            string totalBosses = _wowUtils.GetNumberEmojiFromString((int)guildStats.RaidProgression.ManaforgeOmega.TotalBosses);

            title = $"{guildObject.guildName} on {guildObject.realmName}'s Raider.IO Stats";

            sb.AppendLine("**__Raid Progression:__**");
            sb.AppendLine($"\t **normal** [{normalKilled} / {totalBosses}]");
            sb.AppendLine($"\t **heroic** [{heroicKilled} / {totalBosses}]");
            sb.AppendLine($"\t **mythic** [{mythicKilled} / {totalBosses}]");
            sb.AppendLine();
            sb.AppendLine("**__Raid Rankings:__**");
            sb.AppendLine($"\t **normal** [ realm [**{guildStats.RaidRankings.ManaforgeOmega.Normal.Realm}**] world [**{guildStats.RaidRankings.ManaforgeOmega.Normal.World}**] region [**{guildStats.RaidRankings.ManaforgeOmega.Normal.Region}**] ]");
            sb.AppendLine($"\t **heroic** [ realm [**{guildStats.RaidRankings.ManaforgeOmega.Heroic.Realm}**] world [**{guildStats.RaidRankings.ManaforgeOmega.Heroic.World}**] region [**{guildStats.RaidRankings.ManaforgeOmega.Heroic.Region}**] ]");
            sb.AppendLine($"\t **mythic** [ realm [**{guildStats.RaidRankings.ManaforgeOmega.Mythic.Realm}**] world [**{guildStats.RaidRankings.ManaforgeOmega.Mythic.World}**] region [**{guildStats.RaidRankings.ManaforgeOmega.Mythic.Region}**] ]");
            sb.AppendLine();
            sb.AppendLine($"[{guildObject.guildName} Profile]({guildStats.ProfileUrl.AbsoluteUri})");

            embed.Title = title;
            embed.ThumbnailUrl = thumbUrl;
            embed.WithColor(new Color(0, 0, 255));
            embed.Description = sb.ToString();

            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("setguild", "Associate a WoW guild with this Discord server")]
        public async Task SetGuild(
            [Summary("guild", "Guild name")]
            string guild,

            [Summary("realm", "Realm name (use autocomplete to select)")]
            [Autocomplete(typeof(RealmAutocomplete))]
            string realm,

            [Summary("region", "Region (defaults to US if not specified)")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            [Choice("RU", "ru")]
            string region = "us")
        {
            await DeferAsync(ephemeral: true);

            // Check permissions
            if (Context.Channel is IGuildChannel)
            {
                if (!((IGuildUser)Context.User).GuildPermissions.KickMembers)
                {
                    await FollowupAsync("You need **Kick Members** permission to set the guild association.", ephemeral: true);
                    return;
                }

                // Check cooldown
                var existingAssociation = await _wowUtils.GetGuildAssociation(Context.Guild.Name);
                if (!string.IsNullOrEmpty(existingAssociation?.guildName) && existingAssociation.timeSet.HasValue)
                {
                    var elapsed = DateTime.UtcNow - existingAssociation.timeSet.Value;
                    if (elapsed < GuildChangeCooldown)
                    {
                        var remaining = GuildChangeCooldown - elapsed;
                        await FollowupAsync(
                            $"**Cooldown Active**\nGuild associations can only be changed once every 10 minutes.\n" +
                            $"Please wait **{remaining.Minutes}m {remaining.Seconds}s** before changing the guild association.",
                            ephemeral: true);
                        return;
                    }
                }
            }

            string guildName = guild.Trim();
            string realmName = realm.Trim();
            string regionName = region.ToLower();
            string locale = _wowUtils.GetLocaleFromRegion(ref regionName);
            string discordGuildName = Context.Channel is IDMChannel
                ? Context.User.Username
                : Context.Guild.Name;

            try
            {
                // Verify guild exists on the realm
                await FollowupAsync($"Looking up **{guildName}** on **{realmName}** ({regionName.ToUpper()})...", ephemeral: true);

                GuildMembers members = null;
                try
                {
                    // RealmAutocomplete returns realm slug, so use GetGuildMembersBySlug
                    members = await _wowApi.GetGuildMembersBySlugAsync(realmName, guildName, locale: locale, regionName: regionName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting guild info for {Guild} on {Realm} ({Region})", guildName, realmName, regionName);
                }

                if (members != null && members.guild != null)
                {
                    // Use the official guild/realm names from the API response
                    string officialGuildName = members.guild.name;
                    string officialRealmSlug = members.guild.realm.slug;

                    // Look up the display name for the realm from our database
                    var realmInfo = await _staticDataService.GetRealmBySlugAsync(officialRealmSlug, regionName.ToUpper());
                    string officialRealmName = realmInfo?.Name ?? officialRealmSlug; // Fallback to slug if not found

                    await FollowupAsync($"Found **{officialGuildName}** with **{members.members.Count()}** members!", ephemeral: true);

                    // Save the association with both display name and slug
                    await _wowUtils.SetGuildAssociation(
                        officialGuildName,
                        officialRealmName,
                        officialRealmSlug,
                        locale: locale,
                        regionName: regionName,
                        context: Context);

                    // Refresh guild roster to populate database
                    var guildObject = await _wowUtils.GetGuildName(Context);
                    if (guildObject != null)
                    {
                        try
                        {
                            await _wowUtils.RefreshGuildRosterAsync(guildObject);
                        }
                        catch (Exception rosterEx)
                        {
                            _logger.LogWarning(rosterEx, "Failed to refresh guild roster for {Guild}", officialGuildName);
                        }
                    }

                    var embed = new EmbedBuilder();
                    embed.Title = "Guild Association Set!";
                    embed.WithColor(new Color(0, 200, 150));
                    embed.Description = $"**{discordGuildName}** is now associated with:\n\n" +
                        $"**Guild:** {officialGuildName}\n" +
                        $"**Realm:** {officialRealmName}\n" +
                        $"**Region:** {regionName.ToUpper()}\n" +
                        $"**Members:** {members.members.Count()}\n\n" +
                        $"Use `/getguild` to see the full guild info!";

                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                }
                else
                {
                    var embed = new EmbedBuilder();
                    embed.Title = "Guild Not Found";
                    embed.WithColor(new Color(255, 0, 0));
                    embed.Description = $"Unable to find **{guildName}** on **{realmName}** ({regionName.ToUpper()}).\n\n" +
                        "**Possible reasons:**\n" +
                        "• Guild name is spelled incorrectly\n" +
                        "• Realm is incorrect\n" +
                        "• Wrong region selected\n" +
                        "• Guild doesn't exist or was recently deleted\n\n" +
                        "Please double-check and try again using autocomplete for the realm.";

                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Set-Guild error for {Guild} on {Realm} ({Region})", guildName, realmName, regionName);
                await FollowupAsync($"An error occurred while setting the guild association. Please try again later.", ephemeral: true);
            }
        }

        [SlashCommand("getguild", "Report Discord Server -> Guild Association")]
        public async Task GetGuild()
        {
            var embed = new EmbedBuilder();
            var members = new List<WowGuildRosterMember>();
            StringBuilder sb = new StringBuilder();
            string title = string.Empty;
            string thumbUrl = string.Empty;
            var guildInfo = Context.Guild;
            string discordGuildName = string.Empty;

            if (guildInfo == null)
            {
                discordGuildName = Context.User.Username;
                thumbUrl = Context.User.GetAvatarUrl();
            }
            else
            {
                discordGuildName = Context.Guild.Name;
                thumbUrl = Context.Guild.IconUrl;
            }

            NinjaObjects.GuildObject guildObject = await _wowUtils.GetGuildName(Context);

            title = $"Guild association for **{discordGuildName}**";

            embed.Title = title;
            embed.ThumbnailUrl = thumbUrl;
            if (guildObject.guildName != null)
            {
                try
                {
                    await DeferAsync(ephemeral: true);

                    await _wowUtils.RefreshGuildRosterAsync(guildObject);
                    members = await WithDbAsync(async db => await db.WowGuildRosterMembers
                        .Where(x =>
                            x.GuildName == guildObject.guildName &&
                            x.GuildRealmSlug == guildObject.realmSlug &&
                            x.Region == guildObject.regionName)
                        .ToListAsync());

                }
                catch (Exception ex)
                {
                    _logger.LogError($"Get-Guild Error getting guild info: [{ex.Message}]");
                }
                string guildName = string.Empty;
                string guildRealm = string.Empty;
                string guildRegion = string.Empty;
                string faction = string.Empty;
                switch (members[0].Faction)
                {
                    case "ALLIANCE":
                        {
                            faction = "Alliance";
                            embed.WithColor(new Color(0, 0, 255));
                            break;
                        }
                    case "HORDE":
                        {
                            faction = "Horde";
                            embed.WithColor(new Color(255, 0, 0));
                            break;
                        }
                }
                guildName = members[0].GuildName;
                guildRealm = guildObject.realmName;
                guildRegion = guildObject.regionName;
                sb.AppendLine($"Guild Name: **{guildName}**");
                sb.AppendLine($"Realm Name: **{guildRealm}**");
                sb.AppendLine($"Members: **{members.Count().ToString()}**");
                sb.AppendLine($"Faction: **{faction}**");
                sb.AppendLine($"Region: **{guildRegion}**");
            }
            else
            {
                sb.AppendLine($"No guild association found for **{discordGuildName}**!");
                sb.AppendLine($"Please use /set-guild realmName, guild name, region (optional, defaults to US, valid values are eu or us) to associate a guild with **{discordGuildName}**");
            }
            embed.Description = sb.ToString();
            await FollowupAsync(embed: embed.Build(), ephemeral: true);
        }
    }
}
