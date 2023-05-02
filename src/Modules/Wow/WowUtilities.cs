using NinjaBotCore.Database;
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

namespace NinjaBotCore.Modules.Wow
{
    public class WowUtilities
    {
        public ChannelCheck _cc;
        public WarcraftLogs _logsApi;
        public WowApi _wowApi;
        public DiscordShardedClient _client;
        public RaiderIOApi _rioApi;
        public readonly IConfigurationRoot _config;
        public string _prefix;
        public readonly ILogger _logger;
        
        public WowUtilities(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<WowUtilities>>();
            _cc = services.GetRequiredService<ChannelCheck>();
            _logsApi = services.GetRequiredService<WarcraftLogs>();            
            _wowApi = services.GetRequiredService<WowApi>();
            _rioApi = services.GetRequiredService<RaiderIOApi>();
            _client = services.GetRequiredService<DiscordShardedClient>();            
            _config = services.GetRequiredService<IConfigurationRoot>();
            _prefix = _config["prefix"];
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
                    guildie = _wowApi.GetCharFromGuild(charName, guildObject.realmName, guildObject.guildName, guildObject.regionName);
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
                    chars = _wowApi.SearchArmory(charName);
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
                await _cc.Reply(context, $"Please specify only a character name for the search!");
                return;
            }
            StringBuilder sb = new StringBuilder();
            string charName = args;
            List<FoundChar> found = _wowApi.SearchArmory(charName);
            var embed = new EmbedBuilder();
            embed.Title = $"__WoW Armory Search Results For: **{charName}**__";

            foreach (FoundChar searchFound in found)
            {
                sb.AppendLine($":black_small_square: **{searchFound.charName}** (**{searchFound.level}**) *{searchFound.realmName}*");
            }
            embed.WithColor(new Color(255, 0, 0));
            embed.Description = sb.ToString();
            await _cc.Reply(context, embed);
        }

        public async Task GetAuctionItems(string args, ICommandContext context)
        {
            try
            {
                NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
                string realmName = string.Empty;
                string regionName = "us";
                if (string.IsNullOrEmpty(args))
                {
                    guildObject = await GetGuildName(context);
                    realmName = guildObject.realmName;
                    regionName = guildObject.regionName;
                }
                else
                {
                    int i = 0;
                    do
                    {
                        if (i == args.Split(' ').Count() - 1)
                        {
                            realmName += $"{args.Split(' ')[i]}".Replace("\"", "");
                        }
                        else
                        {
                            realmName += $"{args.Split(' ')[i]} ".Replace("\"", "");
                        }
                        i++;
                    }
                    while (i <= args.Split(' ').Count() - 1);
                    realmName = realmName.Trim();
                }
                if (string.IsNullOrEmpty(realmName))
                {
                    await _cc.Reply(context, $"Unable to find realm \nTry {_prefix}wow auctions realmName");
                    return;
                }
                _logger.LogInformation($"Looking up auctions for realm {realmName.ToUpper()}");
                List<WowAuctions> auctions = await _wowApi.GetAuctionsByRealm(realmName.ToLower(), guildObject.regionName);
                StringBuilder sb = new StringBuilder();
                var auctionList = GetAuctionItemIDs();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(0, 255, 0));
                embed.Title = $":scales:Auction prices on **{realmName.ToUpper()}**";
                foreach (var item in auctionList)
                {
                    var auction = auctions.Where(auc => auc.AuctionItemId == item.ItemID).ToList();
                    long lowestPrice = GetLowestBuyoutPrice(auction.Where(r => r.AuctionBuyout != 0));
                    if (auction.Count() != 0)
                    {
                        sb.AppendLine($":black_small_square:__**{item.Name}**__ / Found **{auction.Count()}** / Lowest price **{ lowestPrice / 10000}g**");
                    }
                    else
                    {
                        sb.AppendLine($":black_small_square:__**{item.Name}**__ / None found :(");
                    }
                }
                sb.AppendLine();
                sb.AppendLine($"Last updated: **{auctions[0].DateModified}**");
                embed.Description = sb.ToString();
                await _cc.Reply(context, embed);
                
                using (var db = new NinjaBotEntities())
                {
                    foreach (var item in auctionList)
                    {                        
                        var items = auctions.Where(auc => auc.AuctionItemId == item.ItemID).ToList();
                        long lowestPrice = GetLowestBuyoutPrice(items.Where(r => r.AuctionBuyout != 0));
                        long highestPrice = GetHighestBuyoutPrice(items.Where(r => r.AuctionBuyout != 0));
                        long averagePrice = GetAveragePrice(items.Where(r => r.AuctionBuyout != 0));
                        if (items.Count() > 0)
                        {
                            db.WowAuctionPrices.Add(new WowAuctionPrice
                            {
                                AuctionItemId = (long)items[0].AuctionItemId,
                                AuctionRealm = items[0].RealmSlug,
                                AvgPrice = averagePrice,
                                MinPrice = lowestPrice,
                                MaxPrice = highestPrice,
                                Seen = items.Count()
                            });
                        }
                    }
                    await db.SaveChangesAsync();
                }                                
            }
            catch (Exception ex)
            {
                _logger.LogError($"Auction error: [{ex.Message}]");
                await _cc.Reply(context, "Error getting auctions :(");
            }
        }

        public List<AuctionList> GetAuctionItemIDs()
        {
            List<AuctionList> auctionItems = new List<AuctionList>();
            using (var db = new NinjaBotEntities())
            {
                var auctionItemList = db.AuctionItemMappings.ToList();
                foreach (var item in auctionItemList)
                {
                    auctionItems.Add(new AuctionList
                    {
                        ItemID = (int)item.ItemId,
                        Name = item.Name
                    });
                }
            }
            return auctionItems;
        }

        public long GetLowestBuyoutPrice(IEnumerable<WowAuctions> auctions)
        {
            long lowestBuyoutPrice = long.MaxValue;
            try
            {
                foreach (var item in auctions)
                {
                    if ((item.AuctionBuyout / item.AuctionQuantity) < lowestBuyoutPrice)
                    {
                        lowestBuyoutPrice = Math.Min(lowestBuyoutPrice, ((long)item.AuctionBuyout / (long)item.AuctionQuantity));
                    }
                }
            }
            catch (DivideByZeroException ex)
            {
                _logger.LogError($"Get Lowest Buyout Error -> [{ex.Message}]");
            }
            return lowestBuyoutPrice;
        }

        public long GetHighestBuyoutPrice(IEnumerable<WowAuctions> auctions)
        {
            long highestBuyoutPrice = long.MinValue;
            try
            {
                foreach (var item in auctions)
                {
                    if ((item.AuctionBuyout / item.AuctionQuantity) > highestBuyoutPrice)
                    {
                        highestBuyoutPrice = Math.Max(highestBuyoutPrice, ((long)item.AuctionBuyout / (long)item.AuctionQuantity));
                    }
                }
            }
            catch (DivideByZeroException ex)
            {
                _logger.LogError($"Get Highest Buyout Error -> [{ex.Message}]");
            }
            return highestBuyoutPrice;
        }

        public long GetAveragePrice(IEnumerable<WowAuctions> auctions)
        {
            long averageBuyoutPrice = 0;
            long? total = 0;
            int totalAuctions = auctions.Count();
            try
            {
                foreach (var item in auctions)
                {
                    total += item.AuctionBuyout / (long)item.AuctionQuantity;
                }
                averageBuyoutPrice = (long)total / totalAuctions;
            }
            catch (DivideByZeroException ex)
            {
                _logger.LogError($"Get Average Buyout Error -> [{ex.Message}]");
            }
            return averageBuyoutPrice;
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
            using (var db = new NinjaBotEntities())
            {
                var foundGuild = db.WowGuildAssociations.FirstOrDefault(g => g.ServerName == discordGuildName);
                if (foundGuild != null)
                {
                    guildObject.guildName = foundGuild.WowGuild;
                    guildObject.realmName = foundGuild.WowRealm;
                    guildObject.regionName = foundGuild.WowRegion;
                    guildObject.locale = foundGuild.Locale;
                    guildObject.realmSlug = foundGuild.LocalRealmSlug;
                }
            }
            return guildObject;
        }

        public async Task SetGuildAssociation(string wowGuildName, string realmName, string locale, string regionName, ICommandContext context)
        {
            try
            {
                var guildInfo = context.Guild;

                string guildName = string.Empty;
                string realmSlug = string.Empty;
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

                //use locale to determine realm slug
                for (int i = 0; i < 500; i++)
                {
                    _logger.LogInformation("Attempting to find slug!");
                    var slugs = _wowApi.GetRealmStatus(locale: locale, region: apiRegion);   
                    realmSlug = realmName;         
                    /*            
                    switch (locale)
                    {
                        case "ru_RU":
                            {                                                        
                                realmSlug = slugs.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.Replace("'","").ToLower())).Select(s => s.slug).FirstOrDefault();
                                break;
                            }
                        case "en_GB":
                            {                            
                                realmSlug = slugs.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.Replace("'","").ToLower())).Select(s => s.slug).FirstOrDefault();
                                break;
                            }
                        case "en_US":
                            {                            
                                realmSlug = slugs.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.Replace("'","").ToLower())).Select(s => s.slug).FirstOrDefault();
                                break;
                            }
                        default: 
                            {                            
                                realmSlug = slugs.realms.Where(r => r.name.Replace("'","").ToLower().Contains(realmName.Replace("'","").ToLower())).Select(s => s.slug).FirstOrDefault();
                                break;
                            }
                    }
                    */
                    if (!string.IsNullOrEmpty(realmSlug))
                    {
                        _logger.LogInformation($"Found slug {realmSlug}!");
                        break;
                    }
                }
            
                using (var db = new NinjaBotEntities())
                {
                    var foundGuild = db.WowGuildAssociations.FirstOrDefault(g => g.ServerName == guildName);

                    if (foundGuild == null)
                    {
                        WowGuildAssociations newGuild = new WowGuildAssociations
                        {
                            ServerId       = (long)guildId,
                            ServerName     = guildName,
                            WowGuild       = wowGuildName,
                            WowRealm       = realmName,
                            WowRegion      = apiRegion,
                            LocalRealmSlug = realmSlug,
                            Locale         = locale,
                            SetBy          = context.User.Username,
                            SetById        = (long)context.User.Id,
                            TimeSet        = DateTime.Now
                        };
                        db.WowGuildAssociations.Add(newGuild);
                    }
                    else
                    {
                        foundGuild.ServerId       = (long)guildId;
                        foundGuild.WowGuild       = wowGuildName;
                        foundGuild.WowRealm       = realmName;
                        foundGuild.WowRegion      = apiRegion;
                        foundGuild.Locale         = locale;
                        foundGuild.LocalRealmSlug = realmSlug;
                        foundGuild.SetBy          = context.User.Username;
                        foundGuild.SetById        = (long)context.User.Id;
                        foundGuild.TimeSet        = DateTime.Now;
                    }
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error setting guild association for {context.Guild.Name} to {wowGuildName}-{realmName} [{ex.Message}]");
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

        public string FindAchievements(Character armoryInfo)
        {
            StringBuilder cheevMessage = new StringBuilder();
            List<FindWowCheeve> findCheeves = null;
            var completedCheeves = armoryInfo.achievements.achievementsCompleted;
            using (var db = new NinjaBotEntities())
            {
                findCheeves = db.FindWowCheeves.ToList();
            }
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

        public int GetEncounterID(string encounterName, string zoneName = "The Nighthold")
        {
            int encounterID = 0;
            var zone = WarcraftLogs.Zones.Where(z => z.name.ToLower() == zoneName.ToLower()).FirstOrDefault();
            if (zone != null)
            {
                encounterID = zone.encounters.Where(e => e.name.ToLower() == encounterName.ToLower()).FirstOrDefault().id;
            }
            return encounterID;
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
            var currentTier       = new CurrentRaidTier();
            currentTier.WclZoneId = zone.id;
            currentTier.RaidName  = zone.name;

            using (var db = new NinjaBotEntities())
            {
                var curRaid = db.CurrentRaidTier.FirstOrDefault();
                if (curRaid == null)
                {
                    await db.CurrentRaidTier.AddAsync(new CurrentRaidTier
                    {
                        WclZoneId = currentTier.WclZoneId,
                        RaidName  = currentTier.RaidName
                    });
                }
                else
                {
                    curRaid.WclZoneId = currentTier.WclZoneId;
                    curRaid.RaidName  = currentTier.RaidName;
                }

                await db.SaveChangesAsync();
            }
            WarcraftLogs.CurrentRaidTier = currentTier;
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
                    guildie = _wowApi.GetCharFromGuild(charName, guildObject.realmName, guildObject.guildName, guildObject.regionName);
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
                    chars = _wowApi.SearchArmory(charName);
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

        public async Task SetGuildAssociation(string wowGuildName, string realmName, string locale, string regionName, ShardedInteractionContext context)
        {
            try
            {
                var guildInfo = context.Guild;

                string guildName = string.Empty;
                string realmSlug = string.Empty;
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
                realmSlug = realmName;               
            
                using (var db = new NinjaBotEntities())
                {
                    var foundGuild = db.WowGuildAssociations.FirstOrDefault(g => g.ServerName == guildName);

                    if (foundGuild == null)
                    {
                        WowGuildAssociations newGuild = new WowGuildAssociations
                        {
                            ServerId       = (long)guildId,
                            ServerName     = guildName,
                            WowGuild       = wowGuildName,
                            WowRealm       = realmName,
                            WowRegion      = apiRegion,
                            LocalRealmSlug = realmSlug,
                            Locale         = locale,
                            SetBy          = context.User.Username,
                            SetById        = (long)context.User.Id,
                            TimeSet        = DateTime.Now
                        };
                        db.WowGuildAssociations.Add(newGuild);
                    }
                    else
                    {
                        foundGuild.ServerId       = (long)guildId;
                        foundGuild.WowGuild       = wowGuildName;
                        foundGuild.WowRealm       = realmName;
                        foundGuild.WowRegion      = apiRegion;
                        foundGuild.Locale         = locale;
                        foundGuild.LocalRealmSlug = realmSlug;
                        foundGuild.SetBy          = context.User.Username;
                        foundGuild.SetById        = (long)context.User.Id;
                        foundGuild.TimeSet        = DateTime.Now;
                    }
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error setting guild association for {context.Guild.Name} to {wowGuildName}-{realmName} [{ex.Message}]");
            }
        }
    }
}