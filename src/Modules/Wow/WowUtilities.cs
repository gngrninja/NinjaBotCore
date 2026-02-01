using NinjaBotCore.Database;
using NinjaBotCore.Repositories;
using NinjaBotCore.Models.Wow;
using Discord;
using Discord.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord.Net;
using Discord.WebSocket;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Common;
using Discord.Interactions;
using System.Threading;
using Microsoft.EntityFrameworkCore;

namespace NinjaBotCore.Modules.Wow
{
    public class WowUtilities
    {
        public WowApi _wowApi;
        public DiscordShardedClient _client;
        public RaiderIOApi _rioApi;
        public readonly IConfigurationRoot _config;
        public readonly ILogger _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly WarcraftLogsV2Client _wclV2Client;

        public WowUtilities(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<WowUtilities>>();
            _wowApi = services.GetRequiredService<WowApi>();
            _rioApi = services.GetRequiredService<RaiderIOApi>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _config = services.GetRequiredService<IConfigurationRoot>();
            _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
            _wclV2Client = services.GetRequiredService<WarcraftLogsV2Client>();
        }

        private IRepository<TEntity> GetRepository<TEntity>() where TEntity : class
        {
            return new Repository<TEntity>(_scopeFactory);
        }

        private IUnitOfWork GetUnitOfWork()
        {
            return new UnitOfWork(_scopeFactory);
        }

        public async Task<GuildChar> GetCharFromArgs(string args, ICommandContext context)
        {
            string regionPattern = "^[a-z]{2}$";
            string charName = string.Empty;
            string realmName = string.Empty;
            string foundRegion = string.Empty;
            Regex matchPattern = new Regex($@"{regionPattern}");
            GuildChar guildie = null;
            List<FoundChar> chars;
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            GuildChar charInfo = new GuildChar
            {
                realmName = string.Empty,
                charName = string.Empty
            };
            int argNumber = args.Split(' ').Count();
            switch (argNumber)
            {
                case 1:
                    {
                        charName = args.Split(' ')[0].Trim();
                        break;
                    }
                case 2:
                    {
                        charName = args.Split(' ')[0].Trim();
                        realmName = args.Split(' ')[1].Trim();
                        break;
                    }
            }
            if (argNumber > 2)
            {
                charName = args.Split(' ')[0].Replace("'", string.Empty).Trim();
                realmName = string.Empty;
                int i = 0;
                do
                {
                    i++;
                    MatchCollection match = matchPattern.Matches(args.Split(' ')[i].ToLower());
                    if (match.Count > 0)
                    {
                        foundRegion = match[0].Value;
                        break;
                    }
                    if (i == argNumber - 1)
                    {
                        realmName += $"{args.Split(' ')[i]}".Replace("\"", "");
                    }
                    else
                    {
                        realmName += $"{args.Split(' ')[i]} ".Replace("\"", "");
                    }
                }
                while (i <= argNumber - 2);
                realmName = realmName.Trim();
            }
            if (string.IsNullOrEmpty(realmName))
            {
                //See if they're a guildie first
                try
                {
                    guildObject = await GetGuildName(context);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error looking up character: {ex.Message}");
                }
                if (guildObject.guildName != null && guildObject.realmName != null)
                {
                    // Use realmSlug for API calls, fallback to slugifying realmName
                    var effectiveRealmSlug = !string.IsNullOrEmpty(guildObject.realmSlug)
                        ? guildObject.realmSlug
                        : guildObject.realmName?.ToLower().Replace(" ", "-").Replace("'", "");

                    guildie = await _wowApi.GetCharFromGuildAsync(charName, effectiveRealmSlug, guildObject.guildName, guildObject.regionName);
                    if (string.IsNullOrEmpty(guildie.charName))
                    {
                        guildie = null;
                    }
                }
                //Check to see if the character is in the guild
                if (guildie != null)
                {
                    charName = guildie.charName;
                    realmName = guildie.realmName;
                    charInfo.regionName = guildie.regionName;
                }
                else
                {
                    chars = await _wowApi.SearchArmoryAsync(charName);
                    if (chars != null)
                    {
                        charName = chars[0].charName;
                        realmName = chars[0].realmName;
                    }
                }
            }
            if (!string.IsNullOrEmpty(foundRegion))
            {
                charInfo.regionName = foundRegion;
            }
            charInfo.charName = charName;
            charInfo.realmName = realmName;
            if (!string.IsNullOrEmpty(guildObject.locale))
            {
                charInfo.locale = guildObject.locale;
            }
            return charInfo;
        }

        public async Task SearchWowChars(string args, ICommandContext context)
        {
            if (args.Split(' ').Count() > 1)
            {
                await context.Channel.SendMessageAsync($"Please specify only a character name for the search!");
                return;
            }
            StringBuilder sb = new StringBuilder();
            string charName = args;
            List<FoundChar> found = await _wowApi.SearchArmoryAsync(charName);
            var embed = new EmbedBuilder();
            embed.Title = $"__WoW Armory Search Results For: **{charName}**__";

            foreach (FoundChar searchFound in found)
            {
                sb.AppendLine($":black_small_square: **{searchFound.charName}** (**{searchFound.level}**) *{searchFound.realmName}*");
            }
            embed.WithColor(new Color(255, 0, 0));
            embed.Description = sb.ToString();
            await context.Channel.SendMessageAsync("", false, embed.Build());
        }

        public async Task<NinjaObjects.GuildObject> GetGuildName(ICommandContext context)
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            try
            {
                if (context.Channel is IDMChannel)
                {
                    guildObject = await GetGuildAssociation(context.User.Username);
                }
                else if (context.Channel is IGuildChannel)
                {
                    guildObject = await GetGuildAssociation(context.Guild.Name);
                }
                _logger.LogInformation($"getGuildName: {context.Channel.Name} : {guildObject.guildName} -> {guildObject.realmName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"getGuildName: {ex.Message}");
            }
            return guildObject;
        }

        public async Task<NinjaObjects.GuildObject> GetGuildAssociation(string discordGuildName)
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();

            await using var guildRepo = GetRepository<WowGuildAssociations>();
            var foundGuild = await guildRepo.FirstOrDefaultAsync(g => g.ServerName == discordGuildName);

            if (foundGuild != null)
            {
                // Lazy backfill: if LocalRealmSlug is NULL, generate from realm name and save
                if (string.IsNullOrEmpty(foundGuild.LocalRealmSlug) && !string.IsNullOrEmpty(foundGuild.WowRealm))
                {
                    foundGuild.LocalRealmSlug = foundGuild.WowRealm.ToLower().Replace(" ", "-").Replace("'", "");
                    guildRepo.Update(foundGuild);
                    await guildRepo.SaveChangesAsync();
                    _logger.LogInformation("Backfilled LocalRealmSlug for guild {Guild} on server {Server}: {Slug}",
                        foundGuild.WowGuild, discordGuildName, foundGuild.LocalRealmSlug);
                }

                guildObject.guildName = foundGuild.WowGuild;
                guildObject.realmName = foundGuild.WowRealm;
                guildObject.regionName = foundGuild.WowRegion;
                guildObject.locale = foundGuild.Locale;
                guildObject.realmSlug = foundGuild.LocalRealmSlug;
                guildObject.timeSet = foundGuild.TimeSet;
            }

            return guildObject;
        }

        public async Task SetGuildAssociation(string wowGuildName, string realmName, string realmSlug, string locale, string regionName, ICommandContext context)
        {
            try
            {
                var guildInfo = context.Guild;

                string guildName = string.Empty;
                string apiRegion = string.Empty;
                ulong guildId;

                //guild in this context is the Discord server
                //this if statement gets the user information if it is a DM, discord server info otherwise
                if (context.Channel is IDMChannel)
                {
                    guildName = context.User.Username;
                    guildId = context.User.Id;
                }
                else
                {
                    guildName = guildInfo.Name;
                    guildId = guildInfo.Id;
                }

                if (regionName.ToLower() == "us")
                {
                    apiRegion = "us";
                }
                else
                {
                    apiRegion = "eu";
                }

                // Use UnitOfWork for multi-entity operation
                await using var uow = GetUnitOfWork();

                // Upsert WowGuildAssociations
                var guildRepo = uow.Repository<WowGuildAssociations>();
                await guildRepo.UpsertAsync(
                    findPredicate: g => g.ServerName == guildName,
                    updateAction: guild =>
                    {
                        guild.ServerId = (long)guildId;
                        guild.WowGuild = wowGuildName;
                        guild.WowRealm = realmName;
                        guild.WowRegion = apiRegion;
                        guild.Locale = locale;
                        guild.LocalRealmSlug = realmSlug;
                        guild.SetBy = context.User.Username;
                        guild.SetById = (long)context.User.Id;
                        guild.TimeSet = DateTime.UtcNow;
                    },
                    createFactory: () => new WowGuildAssociations
                    {
                        ServerId = (long)guildId,
                        ServerName = guildName,
                        WowGuild = wowGuildName,
                        WowRealm = realmName,
                        WowRegion = apiRegion,
                        LocalRealmSlug = realmSlug,
                        Locale = locale,
                        SetBy = context.User.Username,
                        SetById = (long)context.User.Id,
                        TimeSet = DateTime.UtcNow
                    });

                // If log monitoring is enabled, update LatestLogRetail to force into Tier 1
                // This ensures the new guild starts in the active tier for immediate checking
                var monitoringRepo = uow.Repository<LogMonitoring>();
                var logMonitoring = await monitoringRepo.FirstOrDefaultAsync(l => l.ServerId == (long)guildId);
                if (logMonitoring != null && logMonitoring.MonitorLogs)
                {
                    logMonitoring.LatestLogRetail = DateTime.UtcNow;
                    _logger.LogInformation("Updated LatestLogRetail for {GuildName} to force into Tier 1 after guild change", guildName);
                }

                // Save all changes in one transaction
                await uow.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException?.Message;
                if (!string.IsNullOrEmpty(inner))
                {
                    _logger.LogError(ex, "Error setting guild association for {Guild} to {WowGuild}-{Realm}: {InnerMessage}", context.Guild?.Name ?? context.User.Username, wowGuildName, realmName, inner);
                }
                else
                {
                    _logger.LogError(ex, "Error setting guild association for {Guild} to {WowGuild}-{Realm}", context.Guild?.Name ?? context.User.Username, wowGuildName, realmName);
                }
            }
        }

        public string GetLocaleFromRegion(ref string regionName)
        {
            string locale;
            switch (regionName)
            {
                case "na":
                    {
                        locale = "en_US";
                        break;
                    }
                case "us":
                    {
                        locale = "en_US";
                        break;
                    } 
                case "eu":
                    {
                        locale = "en_GB";
                        break;
                    }
                case "gb":
                    {                     
                        locale = "en_GB";
                        break;
                    }
                case "uk":
                    {
                        
                        locale = "en_GB";
                        break;
                    }
                case "ru":
                    {
                        locale = "ru_RU";
                        break;
                    }
                default:
                    {
                        locale = "en_US";
                        break;
                    }
            }
            return locale;
        }

        public async Task<string> FindAchievementsAsync(Character armoryInfo)
        {
            StringBuilder cheevMessage = new StringBuilder();
            var completedCheeves = armoryInfo.achievements.achievementsCompleted;

            // Pattern #1: Create repository on-demand (singleton service)
            await using var achievementRepo = GetRepository<FindWowCheeve>();
            var findCheeves = await achievementRepo.GetAllAsync();
            if (findCheeves != null)
            {
                foreach (int achievement in completedCheeves)
                {
                    var findMe = findCheeves.Where(f => f.AchId == achievement).FirstOrDefault();
                    if (findMe != null)
                    {
                        var matchedCheeve = WowApi.Achievements.Where(c => c.id == findMe.AchId).FirstOrDefault();
                        if (matchedCheeve != null)
                        {
                            cheevMessage.AppendLine($":white_check_mark: {matchedCheeve.name}");
                        }
                    }
                }
            }
            return cheevMessage.ToString();
        }

        public string GetPowerMessage(Character armoryInfo)
        {
            StringBuilder sb = new StringBuilder();
            string powerMessage = string.Empty;
            switch (armoryInfo.stats.powerType)
            {
                case "mana":
                    {
                        powerMessage = $":large_blue_circle:[**{String.Format("{0:#,##0}", armoryInfo.stats.power)}**]";
                        break;
                    }

                case "energy":
                    {
                        powerMessage = $":yellow_heart:[**{String.Format("{0:#,##0}", armoryInfo.stats.power)}**]";
                        break;
                    }
                case "focus":
                    {
                        powerMessage = $":evergreen_tree:[**{String.Format("{0:#,##0}", armoryInfo.stats.power)}]**";
                        break;
                    }
                case "rage":
                    {
                        powerMessage = $":rage:[**{String.Format("{0:#,##0}", armoryInfo.stats.power)}**]";
                        break;
                    }
                case "chi":
                    {
                        powerMessage = $":comet:[**{String.Format("{0:#,##0}", armoryInfo.stats.power)}**]";
                        break;
                    }
                case "runic-power":
                    {
                        powerMessage = $":red_circle:[**{String.Format("{0:#,##0}", armoryInfo.stats.power)}**]";
                        break;
                    }
                case "pain":
                    {
                        powerMessage = $":purple_heart:[**{String.Format("{0:#,##0}", armoryInfo.stats.power)}**]";
                        break;
                    }
            }
            sb.AppendLine($":100:__Statistics__:100:");
            sb.AppendLine($":green_heart:[**{String.Format("{0:#,##0}", armoryInfo.stats.health)}**] / {powerMessage}");
            sb.AppendLine($" Haste **{armoryInfo.stats.hasteRating}**(**{String.Format("{0:0.00}", armoryInfo.stats.haste)}%**) / Crit **{armoryInfo.stats.critRating}**(**{String.Format("{0:0.00}", armoryInfo.stats.crit)}**%)");
            sb.AppendLine($" Mastery **{armoryInfo.stats.masteryRating}**(**{String.Format("{0:0.00}", armoryInfo.stats.mastery)}**%) / Versatility: **{armoryInfo.stats.versatility}**");
            sb.AppendLine($" Stamina **{armoryInfo.stats.sta}** / Intellect **{armoryInfo.stats._int}** / Strength **{armoryInfo.stats.str}** / Agility **{armoryInfo.stats.agi}** / Armor **{armoryInfo.stats.armor}**");
            sb.AppendLine($" Avoidance **{armoryInfo.stats.avoidanceRating}** / Block **{String.Format("{0:0.00}", armoryInfo.stats.block)}**% / Dodge **{String.Format("{0:0.00}", armoryInfo.stats.dodge)}**%/ Parry **{armoryInfo.stats.parryRating}**(**{String.Format("{0:0.00}", armoryInfo.stats.parry)}**%)");
            sb.AppendLine();
            sb.AppendLine($":heavy_division_sign:__Average Item Level__: **{armoryInfo.items.averageItemLevel}** / Equipped: **{armoryInfo.items.averageItemLevelEquipped}**");
            sb.AppendLine($":arrow_down_small:__Lowest Item Level__: **{armoryInfo.lowestItemLevel.itemName}** / **{armoryInfo.lowestItemLevel.itemLevel}**");
            sb.AppendLine($":arrow_up_small:__Highest Item Level__: **{armoryInfo.highestItemLevel.itemName}** / **{armoryInfo.highestItemLevel.itemLevel}**");
            sb.AppendLine($":point_right:__Achievement Points__: **{armoryInfo.achievementPoints}**");
            sb.AppendLine();

            return sb.ToString();
        }

        /// <summary>
        /// Gets encounter ID by name. Uses v2 API to get current raid tier.
        /// </summary>
        public async Task<int> GetEncounterIDAsync(string encounterName)
        {
            try
            {
                var currentTier = await _wclV2Client.GetCurrentRaidTierAsync();
                if (currentTier?.Encounters != null)
                {
                    var encounter = currentTier.Encounters.FirstOrDefault(
                        e => e.Name.Contains(encounterName, StringComparison.OrdinalIgnoreCase));
                    return encounter?.Id ?? 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting encounter ID for {EncounterName}", encounterName);
            }
            return 0;
        }

        public string GetNumberEmojiFromString(int number)
        {
            var numMap = new List<(int, string)>()
                {
                    (1, ":one:"),
                    (2, ":two:"),
                    (3, ":three:"),
                    (4, ":four:"),
                    (5, ":five:"),
                    (6, ":six:"),
                    (7, ":seven:"),
                    (8, ":eight:"),
                    (9, ":nine:"),
                    (0, ":zero:"),
                };

            string numberEmoji = string.Empty;
            string numToString = number.ToString();
            
            foreach (char numChar in numToString)
            {
                numberEmoji += numMap.Where(m => m.Item1 == int.Parse(numChar.ToString())).FirstOrDefault().Item2;
            }
                                                       
            return numberEmoji;
        }
        
        public async Task SetLatestRaid(Zones zone)
        {
            var currentTier = new CurrentRaidTier();
            currentTier.WclZoneId = zone.id;
            currentTier.RaidName = zone.name;

            await using var raidRepo = GetRepository<CurrentRaidTier>();

            // This is a singleton pattern - there's only ever one CurrentRaidTier record
            var curRaid = await raidRepo.FirstOrDefaultAsync(r => true);
            if (curRaid == null)
            {
                await raidRepo.AddAsync(new CurrentRaidTier
                {
                    WclZoneId = currentTier.WclZoneId,
                    RaidName = currentTier.RaidName
                });
            }
            else
            {
                curRaid.WclZoneId = currentTier.WclZoneId;
                curRaid.RaidName = currentTier.RaidName;
            }

            await raidRepo.SaveChangesAsync();
        }

        private Color GetEmbedColorFromClass(string className)
        {
            var color = new Color(0, 0, 255);
            switch (className)
            {
                case "monk":
                    {
                        color = new Color(0, 255, 0);
                        break;
                    }
                case "druid":
                    {
                        color = new Color(214, 122, 2);
                        break;
                    }
                case "death knight":
                    {
                        color=new Color(255, 0, 0);
                        break;
                    }
                case "demon hunter":
                    {
                        color = new Color(140, 0, 126);
                        break;
                    }
                case "hunter":
                    {
                        color = new Color(0, 255, 0);
                        break;
                    }
                case "mage":
                    {
                        color = new Color(0, 250, 255);
                        break;
                    }
                case "paladin":
                    {
                        color = new Color(255, 0, 220);
                        break;
                    }
                case "priest":
                    {
                        color = new Color(255, 255, 255);
                        break;
                    }
                case "rogue":
                    {
                        color = new Color(255, 255, 2);
                        break;
                    }
                case "shaman":
                    {
                        color = new Color(0, 0, 255);
                        break;
                    }
                case "warlock":
                    {
                        color = new Color(72, 0, 168);
                        break;
                    }
                case "warrior":
                    {
                        color = new Color(119, 55, 0);
                        break;
                    }
            }
            return color;
        }  
        
        public async Task<GuildChar> GetCharFromArgs(string args, ShardedInteractionContext context)
        {
             string regionPattern = "^[a-z]{2}$";
            string charName = string.Empty;
            string realmName = string.Empty;
            string foundRegion = string.Empty;
            Regex matchPattern = new Regex($@"{regionPattern}");
            GuildChar guildie = null;
            List<FoundChar> chars;
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            GuildChar charInfo = new GuildChar
            {
                realmName = string.Empty,
                charName = string.Empty
            };
            int argNumber = args.Split(' ').Count();
            switch (argNumber)
            {
                case 1:
                    {
                        charName = args.Split(' ')[0].Trim();
                        break;
                    }
                case 2:
                    {
                        charName = args.Split(' ')[0].Trim();
                        realmName = args.Split(' ')[1].Trim();
                        break;
                    }
            }
            if (argNumber > 2)
            {
                charName = args.Split(' ')[0].Replace("'", string.Empty).Trim();
                realmName = string.Empty;
                int i = 0;
                do
                {
                    i++;
                    MatchCollection match = matchPattern.Matches(args.Split(' ')[i].ToLower());
                    if (match.Count > 0)
                    {
                        foundRegion = match[0].Value;
                        break;
                    }
                    if (i == argNumber - 1)
                    {
                        realmName += $"{args.Split(' ')[i]}".Replace("\"", "");
                    }
                    else
                    {
                        realmName += $"{args.Split(' ')[i]} ".Replace("\"", "");
                    }
                }
                while (i <= argNumber - 2);
                realmName = realmName.Trim();
            }
            if (string.IsNullOrEmpty(realmName))
            {
                //See if they're a guildie first
                try
                {
                    guildObject = await GetGuildName(context);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error looking up character: {ex.Message}");
                }
                if (guildObject.guildName != null && guildObject.realmName != null)
                {
                    // Use realmSlug for API calls, fallback to slugifying realmName
                    var effectiveRealmSlug = !string.IsNullOrEmpty(guildObject.realmSlug)
                        ? guildObject.realmSlug
                        : guildObject.realmName?.ToLower().Replace(" ", "-").Replace("'", "");

                    guildie = await _wowApi.GetCharFromGuildAsync(charName, effectiveRealmSlug, guildObject.guildName, guildObject.regionName);
                    if (string.IsNullOrEmpty(guildie.charName))
                    {
                        guildie = null;
                    }
                }
                //Check to see if the character is in the guild
                if (guildie != null)
                {
                    charName = guildie.charName;
                    realmName = guildie.realmName;
                    charInfo.regionName = guildie.regionName;
                }
                else
                {
                    chars = await _wowApi.SearchArmoryAsync(charName);
                    if (chars != null)
                    {
                        charName = chars[0].charName;
                        realmName = chars[0].realmName;
                    }
                }
            }
            if (!string.IsNullOrEmpty(foundRegion))
            {
                charInfo.regionName = foundRegion;
            }
            charInfo.charName = charName;
            charInfo.realmName = realmName;
            if (!string.IsNullOrEmpty(guildObject.locale))
            {
                charInfo.locale = guildObject.locale;
            }
            return charInfo;
        }

        public async Task<NinjaObjects.GuildObject> GetGuildName(ShardedInteractionContext context)
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            try
            {
                if (context.Channel is IDMChannel)
                {
                    guildObject = await GetGuildAssociation(context.User.Username);
                }
                else if (context.Channel is IGuildChannel)
                {
                    guildObject = await GetGuildAssociation(context.Guild.Name);
                }
                _logger.LogInformation($"getGuildName: {context.Channel.Name} : {guildObject.guildName} -> {guildObject.realmName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"getGuildName: {ex.Message}");
            }
            return guildObject;
        }

        public async Task SetGuildAssociation(string wowGuildName, string realmName, string realmSlug, string locale, string regionName, ShardedInteractionContext context)
        {
            try
            {
                var guildInfo = context.Guild;

                string guildName = string.Empty;
                string apiRegion = string.Empty;
                ulong guildId;

                //guild in this context is the Discord server
                //this if statement gets the user information if it is a DM, discord server info otherwise
                if (context.Channel is IDMChannel)
                {
                    guildName = context.User.Username;
                    guildId = context.User.Id;
                }
                else
                {
                    guildName = guildInfo.Name;
                    guildId = guildInfo.Id;
                }

                if (regionName.ToLower() == "us")
                {
                    apiRegion = "us";
                }
                else
                {
                    apiRegion = "eu";
                }

                // Use UnitOfWork for multi-entity operation
                await using var uow = GetUnitOfWork();

                // Upsert WowGuildAssociations
                var guildRepo = uow.Repository<WowGuildAssociations>();
                await guildRepo.UpsertAsync(
                    findPredicate: g => g.ServerName == guildName,
                    updateAction: guild =>
                    {
                        guild.ServerId = (long)guildId;
                        guild.WowGuild = wowGuildName;
                        guild.WowRealm = realmName;
                        guild.WowRegion = apiRegion;
                        guild.Locale = locale;
                        guild.LocalRealmSlug = realmSlug;
                        guild.SetBy = context.User.Username;
                        guild.SetById = (long)context.User.Id;
                        guild.TimeSet = DateTime.UtcNow;
                    },
                    createFactory: () => new WowGuildAssociations
                    {
                        ServerId = (long)guildId,
                        ServerName = guildName,
                        WowGuild = wowGuildName,
                        WowRealm = realmName,
                        WowRegion = apiRegion,
                        LocalRealmSlug = realmSlug,
                        Locale = locale,
                        SetBy = context.User.Username,
                        SetById = (long)context.User.Id,
                        TimeSet = DateTime.UtcNow
                    });

                // If log monitoring is enabled, update LatestLogRetail to force into Tier 1
                // This ensures the new guild starts in the active tier for immediate checking
                var monitoringRepo = uow.Repository<LogMonitoring>();
                var logMonitoring = await monitoringRepo.FirstOrDefaultAsync(l => l.ServerId == (long)guildId);
                if (logMonitoring != null && logMonitoring.MonitorLogs)
                {
                    logMonitoring.LatestLogRetail = DateTime.UtcNow;
                    _logger.LogInformation("Updated LatestLogRetail for {GuildName} to force into Tier 1 after guild change", guildName);
                }

                // Save all changes in one transaction
                await uow.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException?.Message;
                if (!string.IsNullOrEmpty(inner))
                {
                    _logger.LogError(ex, "Error setting guild association for {Guild} to {WowGuild}-{Realm}: {InnerMessage}", context.Guild?.Name ?? context.User.Username, wowGuildName, realmName, inner);
                }
                else
                {
                    _logger.LogError(ex, "Error setting guild association for {Guild} to {WowGuild}-{Realm}", context.Guild?.Name ?? context.User.Username, wowGuildName, realmName);
                }
            }
        }
        
        public async Task RefreshGuildRosterAsync(
            NinjaBotCore.Models.Wow.NinjaObjects.GuildObject guildObject,
            CancellationToken cancellationToken = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var now = DateTime.UtcNow;

            // 🔹 Cache guard
            var lastFetch = await db.WowGuildRosterMembers
                .Where(x =>
                    x.GuildName == guildObject.guildName &&
                    x.GuildRealmSlug == guildObject.realmSlug &&
                    x.Region == guildObject.regionName)
                .MaxAsync(x => (DateTime?)x.LastUpdated, cancellationToken);

            if (lastFetch.HasValue && lastFetch > now.AddMinutes(-60))
                return;

            // 🔹 Fetch API first
            var apiResult = await _wowApi.GetGuildMembersBySlugAsync(
                guildObject.realmSlug,
                guildObject.guildName,
                locale: guildObject.locale,
                regionName: guildObject.regionName,
                cancellationToken: cancellationToken);

            // 🔹 Load existing roster to preserve M+ scores
            var existingMembers = await db.WowGuildRosterMembers
                .Where(x =>
                    x.GuildName == guildObject.guildName &&
                    x.GuildRealmSlug == guildObject.realmSlug &&
                    x.Region == guildObject.regionName)
                .ToDictionaryAsync(
                    x => $"{x.CharacterName.ToLower()}|{x.RealmSlug.ToLower()}",
                    cancellationToken);

            // 🔹 Build set of current member keys from API
            var currentMemberKeys = new HashSet<string>();

            foreach (var m in apiResult.members)
            {
                var key = $"{m.character.name.ToLower()}|{m.character.realm.slug.ToLower()}";
                currentMemberKeys.Add(key);

                if (existingMembers.TryGetValue(key, out var existing))
                {
                    // Update existing member (preserve MythicPlusScore and ItemLevel)
                    existing.Level = m.character.level;
                    existing.Rank = m.rank;
                    existing.Faction = m.character.faction.type;
                    existing.ClassId = m.character.ClassId;
                    existing.LastUpdated = now;
                    // MythicPlusScore and ItemLevel are preserved
                }
                else
                {
                    // Add new member
                    db.WowGuildRosterMembers.Add(new WowGuildRosterMember
                    {
                        GuildName = guildObject.guildName,
                        RealmSlug = m.character.realm.slug,
                        GuildRealmSlug = guildObject.realmSlug,
                        Region = guildObject.regionName,
                        CharacterName = m.character.name,
                        Level = m.character.level,
                        Rank = m.rank,
                        Faction = m.character.faction.type,
                        ClassId = m.character.ClassId,
                        LastUpdated = now
                    });
                }
            }

            // 🔹 Remove members no longer in guild
            var membersToRemove = existingMembers
                .Where(kvp => !currentMemberKeys.Contains(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToList();

            if (membersToRemove.Any())
            {
                db.WowGuildRosterMembers.RemoveRange(membersToRemove);
            }

            await db.SaveChangesAsync(cancellationToken);
        }    
    }
}
