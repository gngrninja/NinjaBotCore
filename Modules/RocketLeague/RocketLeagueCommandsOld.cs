using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord.Net;
using Discord;
using Discord.Commands;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Steam;
using NinjaBotCore.Modules.Steam;
using Discord.WebSocket;
using NinjaBotCore.Models.RocketLeague;
using NinjaBotCore.Modules.RocketLeague;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Services;
using RLSApi;
using RLSApi.Data;
using RLSApi.Net.Requests;

namespace NinjaBotCore.Modules.RocketLeague
{
    public class RlCommands : ModuleBase
    {
        private SteamApi _steam;
        private static ChannelCheck _cc;
        private string _rlStatsKey;
        private readonly IConfigurationRoot _config;
        private string _prefix;
        private RLSClient _rlsClient; 

        public RlCommands(SteamApi steam, ChannelCheck cc, IConfigurationRoot config)
        {          
            _steam = steam;                           
            _cc = cc;                            
            _config = config;
            _rlStatsKey = $"{_config["RlStatsApi"]}";
            _prefix = _config["prefix"];
            _rlsClient = new RLSClient(_rlStatsKey);
        }

        [Command("rl-search", RunMode = RunMode.Async)]
        public async Task SearchRlPlayers([Remainder]string searchPlayer = null)
        {
            var embed = new EmbedBuilder();
            embed.Title = "RL Stats Search";
            if (searchPlayer != null)
            {
                StringBuilder sb = new StringBuilder();
                var searchResults = await _rlsClient.SearchPlayerAsync(searchPlayer);
                if (searchResults != null)
                {
                    if (searchResults.TotalResults > 1)
                    {
                        sb.AppendLine($"**Use the {_prefix}rlstats command with one of the following player names:**");
                        for (int i = 0; i <= searchResults.Data.Count() - 1; i++)
                        {
                            DateTime humanTime = searchResults.Data[i].UpdatedAt.UnixTimeStampToDateTimeSeconds();
                            embed.AddField(new EmbedFieldBuilder
                            {
                                Name = $":white_small_square: {searchResults.Data[i].DisplayName}",
                                Value = $"Last Updated: [*{string.Format("{0:MM-dd-yy HH:mm}", humanTime)}*]",
                                IsInline = true
                            });
                        }
                    }
                    else if (searchResults.Data.Count() == 1)
                    {
                        DateTime humanTime = searchResults.Data[0].UpdatedAt.UnixTimeStampToDateTimeSeconds();
                        embed.AddField(new EmbedFieldBuilder
                        {
                            Name = $":white_small_square: {searchResults.Data[0].DisplayName}",
                            Value = $"Last Updated: [*{string.Format("{0:MM-dd-yy HH:mm}", humanTime)}*]",
                            IsInline = true
                        });
                    }
                    else
                    {
                        sb.AppendLine($"No players found with that name!");
                    }
                }
                embed.Description = sb.ToString();
            }
            else
            {
                embed.Description = $"Please specify a user name to search for!";
            }
            await _cc.Reply(Context, embed);
        }

        [Command("rltest", RunMode = RunMode.Async)]
        public async Task RlTest([Remainder] string playerName)
        {
            var player = await _rlsClient.SearchPlayerAsync(playerName);  
            if (player.TotalResults > 0)
            {
                var seasonSeven = player.Data[0].RankedSeasons.FirstOrDefault(x => x.Key == RlsSeason.Seven);            
                if (seasonSeven.Value != null)
                {
                    System.Console.WriteLine($"{player.Data.FirstOrDefault().DisplayName}");

                    foreach (var playerRank in seasonSeven.Value)
                    {
                        System.Console.WriteLine($"{playerRank.Key} -> {playerRank.Value.RankPoints}");
                    }
                }
            }                      
            //string displayName = player.Data[0].DisplayName;
            //await _cc.Reply(Context, $"{displayName}");
        }

        [Command("rlstats", RunMode = RunMode.Async)]
        [Summary("Get Rocket League Stats. Use this command with set, followed by your steam URL/ID/VanityName to set a default user (rlstats set URL/ID/VanityName")]
        public async Task RlStats([Remainder]string args = null)
        {
            StringBuilder sb = new StringBuilder();
            var rlStats = new RlStat();
            if (!string.IsNullOrEmpty(args))
            {
                if (args.Split(' ').Count() > 1)
                {
                    string arg = args.Split(' ')[1].ToLower().ToString();
                    switch (args.Split(' ')[0].ToString().ToLower())
                    {
                        case "get":
                            {
                                if (!string.IsNullOrEmpty(arg))
                                {
                                    await GetStats(arg);
                                }
                                else
                                {
                                    await _cc.Reply(Context, "Please specify a steam ID/vanity name to get the stats of!");
                                }
                                break;
                            }
                        case "set":
                            {
                                await SetStats(arg);
                                break;
                            }
                        case "help":
                            {
                                break;
                            }
                    }
                }
                else
                {
                    sb.AppendLine($"Please specify a name / steamID after using the set/get commands!");
                    await _cc.Reply(Context, sb.ToString());
                    return;
                }
            }
            else
            {
                var rlUser = await GetRlUserInfo(false);
                if (rlUser == null)
                {
                    sb.AppendLine($"Unable to find steam name association for discord user {rlUser.DiscordUserName}");
                    await _cc.Reply(Context, sb.ToString());
                    return;
                }
                else
                {
                    await GetStats(rlUser.SteamID);
                }
            }
        }

        public async Task SetStats(string name)
        {
            try
            {
                using (var db = new NinjaBotEntities())
                {
                    string channel = Context.Channel.Name;
                    string userName = Context.User.Username;
                    StringBuilder sb = new StringBuilder();
                    string rlUserName = name;

                    if (Uri.IsWellFormedUriString(rlUserName, UriKind.RelativeOrAbsolute))
                    {
                        rlUserName = rlUserName.TrimEnd('/');
                        rlUserName = rlUserName.Substring(rlUserName.LastIndexOf('/') + 1);
                    }

                    SteamModel.Player fromSteam = _steam.GetSteamPlayerInfo(rlUserName);
                    if (string.IsNullOrEmpty(fromSteam.steamid))
                    {
                        sb.AppendLine($"{Context.User.Mention}, Please specify a steam username/full profile URL to link with your Discord username!");
                        await _cc.Reply(Context, sb.ToString());
                        return;
                    }
                    try
                    {
                        var addUser = new RlStat();
                        var rlUser = db.RlStats.Where(r => r.DiscordUserName == userName).FirstOrDefault();
                        if (rlUser == null)
                        {
                            addUser.DiscordUserName = userName;
                            addUser.SteamID = long.Parse(fromSteam.steamid);
                            addUser.DiscordUserID = (long)Context.User.Id;
                            db.RlStats.Add(addUser);
                        }
                        else
                        {
                            rlUser.SteamID = long.Parse(fromSteam.steamid);
                            rlUser.DiscordUserID = (long)Context.User.Id;
                            //rl.setUserName(userName, fromSteam.steamid);
                        }
                        db.SaveChanges();
                        sb.AppendLine($"{Context.User.Mention}, you've associated [**{fromSteam.personaname}**] with your Discord name!");
                        await _cc.Reply(Context, sb.ToString());
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"RL Stats: Error setting name -> {ex.Message}");
                        sb.AppendLine($"{Context.User.Mention}, something went wrong, sorry :(");
                        await _cc.Reply(Context, sb.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex.Message}");
            }
        }

        public async Task GetStats(string name)
        {
            StringBuilder sb = new StringBuilder();
            //SteamModel.Player fromSteam = _steam.getSteamPlayerInfo(name);\            
            if (name.Contains("/:") && Uri.IsWellFormedUriString(name, UriKind.RelativeOrAbsolute))
            {
                name = name.TrimEnd('/');
                name = name.Substring(name.LastIndexOf('/') + 1);
                SteamModel.Player fromSteam = _steam.GetSteamPlayerInfo(name);
                try
                {
                    EmbedBuilder embed = await RlEmbedApi(fromSteam);
                    await _cc.Reply(Context, embed);
                }
                catch (Exception ex)
                {
                    await _cc.Reply(Context, "Sorry, something went wrong!");
                    Console.WriteLine(ex.Message);
                }
                return;
            }
            
            var searchResults = await _rlsClient.SearchPlayerAsync(name);
            if (searchResults.TotalResults > 0)
            {
                try
                {
                    EmbedBuilder embed = await RlEmbedApi(searchResults.Data[0]);
                    //await InsertStats(searchResults.data[0]);
                    await _cc.Reply(Context, embed);
                }
                catch (Exception ex)
                {
                    await _cc.Reply(Context, "Sorry, something went wrong!");
                    Console.WriteLine(ex.Message);
                }
                return;
            }
            else
            {
                sb.AppendLine($"Unable to find steam user for steam name/id: {name}!");
                await _cc.Reply(Context, sb.ToString());
                return;
            }
        }

        public async Task GetStats(long? uniqueId)
        {
            StringBuilder sb = new StringBuilder();          
            var userStats = await _rlsClient.GetPlayerAsync(RlsPlatform.Steam, uniqueId.ToString());            
            if (userStats != null)
            {
                try
                {
                    EmbedBuilder embed = await RlEmbedApi(userStats);
                    //await InsertStats(userStats);
                    await _cc.Reply(Context, embed);
                }
                catch (Exception ex)
                {
                    await _cc.Reply(Context, "Sorry, something went wrong!");
                    Console.WriteLine(ex.Message);
                }
                return;
            }
            else
            {
                sb.AppendLine($"Unable to find steam user for steam name/id: {uniqueId}!");
                await _cc.Reply(Context, sb.ToString());
                return;
            }
        }

        private static void FormatStatInfo(StringBuilder sb, RLSApi.Data.RlsPlaylistRanked rank, RLSApi.Net.Models.PlayerRank rankInfo)
        {
            string rankMoji = string.Empty;            
            int? matchesPlayed = rankInfo.MatchesPlayed;
            int? rankPoints = rankInfo.RankPoints;
            string tier = RlStatsApi.Tiers.Where(t => t.tierId == rankInfo.Tier).Select(t => t.tierName).FirstOrDefault();
            if (rankInfo.Tier >= 1 & rankInfo.Tier < 4)
            {
                rankMoji = ":tangerine:";
            }
            else if (rankInfo.Tier >= 4 & rankInfo.Tier < 7)
            {
                rankMoji = ":full_moon:";
            }
            else if (rankInfo.Tier >= 7 & rankInfo.Tier < 10)
            {
                rankMoji = ":sunny:";
            }
            else if (rankInfo.Tier >= 10 & rankInfo.Tier < 13)
            {
                rankMoji = ":fork_knife_plate:";
            }
            else if (rankInfo.Tier >= 13 & rankInfo.Tier < 16)
            {
                rankMoji = ":large_blue_diamond:";
            }
            else if (rankInfo.Tier >= 16 & rankInfo.Tier < 19)
            {
                rankMoji = ":purple_heart:";
            }
            else if (rankInfo.Tier == 19)
            {
                rankMoji = ":eggplant:";
            }
            else
            {
                rankMoji = ":question:";
            }
            if (string.IsNullOrEmpty(tier))
            {
                tier = ":(";
            }
            switch (rank)
            {
                case RlsPlaylistRanked.Duel:
                    {
                        sb.AppendLine("Duel");
                        break;
                    }
                case RlsPlaylistRanked.Doubles:
                    {
                        sb.AppendLine("Doubles");
                        break;
                    }
                case RlsPlaylistRanked.SoloStandard:
                    {
                        sb.AppendLine("Solo Standard");
                        break;
                    }
                case RlsPlaylistRanked.Standard:
                    {
                        sb.AppendLine("Standard");
                        break;
                    }
                default:
                    {
                        sb.AppendLine(rank.ToString());
                        break;
                    }
            }
            sb.AppendLine($"{rankMoji} [*{tier}(div {rankInfo.Division + 1})* [**{rankPoints}**]] Matches played [**{matchesPlayed}**]");
        }

        public async Task<RlStat> GetRlUserInfo(bool ps)
        {
            string userName = Context.User.Username;
            StringBuilder sb = new StringBuilder();
            RlStat rlUser = null;
            using (var db = new NinjaBotEntities())
            {
                rlUser = db.RlStats.FirstOrDefault(r => r.DiscordUserName == userName);
            }
            return rlUser;
        }

        private async Task<EmbedBuilder> RlEmbedApi(RLSApi.Net.Models.Player stats)
        {
            long steamId = 0;
            long.TryParse(stats.UniqueId, out steamId);
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            embed.Title = $"Stats for [{stats.DisplayName}]";
            embed.ThumbnailUrl = stats.Avatar;
            embed.WithColor(new Color(0, 255, 0));
            embed.WithAuthor(new EmbedAuthorBuilder
            {
                Name = $"{Context.User.Username}",
                IconUrl = $"{Context.User.GetAvatarUrl()}"
            });
            //var rankings = Enum.GetValues(typeof(RlRankingList));
            var rankingsFromObject = stats.RankedSeasons.FirstOrDefault(k => k.Key == RlsSeason.Seven);
            foreach (var rank in rankingsFromObject.Value)
            {                                
                FormatStatInfo(sb, rank.Key, rank.Value);
            }
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Season 7",
                Value = $"{sb.ToString()}",
                IsInline = false
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Shots",
                Value = $"{stats.Stats.Shots}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Goals",
                Value = $"{stats.Stats.Goals}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "MVPs",
                Value = $"{stats.Stats.Mvps}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Assists",
                Value = $"{stats.Stats.Assists}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Saves",
                Value = $"{stats.Stats.Saves}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Wins",
                Value = $"{stats.Stats.Wins}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Stats Provided By:",
                Value = $"{stats.ProfileUrl}",
                IsInline = false
            });
            return embed;
        }

        private async Task<EmbedBuilder> RlEmbedApi(SteamModel.Player fromSteam)
        {
            long steamId = 0;
            long.TryParse(fromSteam.steamid, out steamId);
            var stats = await _rlsClient.GetPlayerAsync(RlsPlatform.Steam, steamId.ToString());
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            embed.Title = $"Stats for [{fromSteam.personaname}]";
            embed.ThumbnailUrl = fromSteam.avatarfull;
            embed.WithColor(new Color(0, 255, 0));
            embed.WithAuthor(new EmbedAuthorBuilder
            {
                Name = $"{Context.User.Username}",
                IconUrl = $"{Context.User.GetAvatarUrl()}"
            });            
            var rankingsFromObject = stats.RankedSeasons.FirstOrDefault(x => x.Key == RlsSeason.Seven);
            foreach (var rank in rankingsFromObject.Value)
            {
                //var rankInfo = (SeasonStats)rank.Value;
                //FormatStatInfo(sb, rank, rankInfo);
            }
            /* 
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Season 7",
                Value = $"{sb.ToString()}",
                IsInline = false
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Shots",
                Value = $"{stats.stats.shots}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Goals",
                Value = $"{stats.stats.goals}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "MVPs",
                Value = $"{stats.stats.mvps}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Assists",
                Value = $"{stats.stats.assists}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Saves",
                Value = $"{stats.stats.saves}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Wins",
                Value = $"{stats.stats.wins}",
                IsInline = true
            });
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Stats Provided By:",
                Value = $"{stats.profileUrl}",
                IsInline = false
            });
            */
            return embed;
        }

        private async Task InsertStats(List<RocketLeagueStats> stats, SteamModel.Player fromSteam)
        {
            long steamId = long.Parse(fromSteam.steamid);
            using (var db = new NinjaBotEntities())
            {
                var statAdd = new RlUserStat();
                var getStat = db.RlUserStats.Where(s => s.SteamID == steamId).FirstOrDefault();
                var doubles = stats.Where(s => s.Title == "Doubles 2v2").FirstOrDefault();
                var duel = stats.Where(s => s.Title == "Duel 1v1").FirstOrDefault();
                var soloStandard = stats.Where(s => s.Title == "Solo Standard 3v3").FirstOrDefault();
                var rankedThrees = stats.Where(s => s.Title == "Standard 3v3").FirstOrDefault();
                var unranked = stats.Where(s => s.Title == "Unranked").FirstOrDefault();

                if (getStat == null)
                {
                    if (doubles != null)
                    {
                        statAdd.Ranked2v2 = doubles.MMR.ToString();
                    }
                    else
                    {
                        statAdd.Ranked2v2 = "0";
                    }
                    if (duel != null)
                    {
                        statAdd.RankedDuel = duel.MMR.ToString();
                    }
                    else
                    {
                        statAdd.RankedDuel = "0";
                    }
                    if (soloStandard != null)
                    {
                        statAdd.RankedSolo = soloStandard.MMR.ToString();
                    }
                    else
                    {
                        statAdd.RankedSolo = "0";
                    }
                    if (rankedThrees != null)
                    {
                        statAdd.Ranked3v3 = rankedThrees.MMR.ToString();
                    }
                    else
                    {
                        statAdd.Ranked3v3 = "0";
                    }
                    if (unranked != null)
                    {
                        statAdd.Unranked = unranked.MMR.ToString();
                    }
                    else
                    {
                        statAdd.Unranked = "0";
                    }
                    statAdd.SteamID = steamId;
                    db.RlUserStats.Add(statAdd);
                }
                else
                {
                    if (doubles != null)
                    {
                        getStat.Ranked2v2 = doubles.MMR.ToString();
                    }

                    if (duel != null)
                    {
                        getStat.RankedDuel = duel.MMR.ToString();
                    }

                    if (soloStandard != null)
                    {
                        getStat.RankedSolo = soloStandard.MMR.ToString();
                    }

                    if (rankedThrees != null)
                    {
                        getStat.Ranked3v3 = rankedThrees.MMR.ToString();
                    }

                    if (unranked != null)
                    {
                        getStat.Unranked = unranked.MMR.ToString();
                    }
                    getStat.SteamID = steamId;
                }
                await db.SaveChangesAsync();
            }
        }

        private async Task InsertStats(UserStats stats)
        {
            long steamId = long.Parse(stats.uniqueId);
            using (var db = new NinjaBotEntities())
            {
                var statAdd = new RlUserStat();
                var getStat = db.RlUserStats.Where(s => s.SteamID == steamId).FirstOrDefault();
                if (stats.rankedSeasons._6._onevone != null)
                {
                    statAdd.RankedDuel = stats.rankedSeasons._6._onevone.RankPoints.ToString();
                }
                if (stats.rankedSeasons._6._twovtwo != null)
                {
                    statAdd.Ranked2v2 = stats.rankedSeasons._6._twovtwo.RankPoints.ToString();
                }
                if (stats.rankedSeasons._6._solostandard != null)
                {
                    statAdd.RankedSolo = stats.rankedSeasons._6._solostandard.RankPoints.ToString();
                }
                if (stats.rankedSeasons._6._standard != null)
                {
                    statAdd.Ranked3v3 = stats.rankedSeasons._6._standard.RankPoints.ToString();
                }
                if (getStat == null)
                {
                    statAdd.SteamID = steamId;
                    db.RlUserStats.Add(statAdd);
                }
                else
                {
                    if (stats.rankedSeasons._6._onevone != null)
                    {
                        getStat.RankedDuel = stats.rankedSeasons._6._onevone.RankPoints.ToString();
                    }
                    if (stats.rankedSeasons._6._twovtwo != null)
                    {
                        getStat.Ranked2v2 = stats.rankedSeasons._6._twovtwo.RankPoints.ToString();
                    }
                    if (stats.rankedSeasons._6._solostandard != null)
                    {
                        getStat.RankedSolo = stats.rankedSeasons._6._solostandard.RankPoints.ToString();
                    }
                    if (stats.rankedSeasons._6._standard != null)
                    {
                        getStat.Ranked3v3 = stats.rankedSeasons._6._standard.RankPoints.ToString();
                    }
                    getStat.SteamID = steamId;
                }
                await db.SaveChangesAsync();
            }
        }

        private async Task<RlUserStat> GetStatsFromDb(SteamModel.Player fromSteam)
        {
            RlUserStat rlUserStats = null;
            long steamId = long.Parse(fromSteam.steamid);
            using (var db = new NinjaBotEntities())
            {
                rlUserStats = db.RlUserStats.Where(r => r.SteamID == steamId).FirstOrDefault();
            }
            return rlUserStats;
        }
    }
}