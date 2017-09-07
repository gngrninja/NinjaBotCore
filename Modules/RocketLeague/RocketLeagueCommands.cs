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

namespace NinjaBotCore.Modules.RocketLeague
{
    public class RlCommands : ModuleBase
    {
        private Steam.Steam _steam = null;
        private RocketLeague _rl = null;
        private static ChannelCheck _cc = null;
        private RlStatsApi _rlStatsApi = null;

        public RlCommands(Steam.Steam steam, ChannelCheck cc, RocketLeague rl, RlStatsApi rlStatsApi)
        {
            try
            {
                if (_steam == null)
                {
                    _steam = steam;
                }
                if (_rl == null)
                {
                    _rl = rl;
                }
                if (_cc == null)
                {
                    _cc = cc;
                }
                if (_rlStatsApi == null)
                {
                    _rlStatsApi = rlStatsApi;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unable to create Rocket League Commands class ->  [{ex.Message}]");
            }
        }

        [Command("rl-search", RunMode = RunMode.Async)]
        public async Task SearchRlPlayers([Remainder]string searchPlayer = null)
        {
            var embed = new EmbedBuilder();
            embed.Title = "RL Stats Search";
            if (searchPlayer != null)
            {
                StringBuilder sb = new StringBuilder();
                var searchResults = await _rlStatsApi.SearchForPlayer(searchPlayer);
                if (searchResults != null)
                {
                    if (searchResults.data.Count > 1)
                    {
                        sb.AppendLine($"**Use the {Config.Prefix}rlstats command with one of the following player names:**");
                        for (int i = 0; i <= searchResults.data.Count - 1; i++)
                        {
                            DateTime humanTime = _rlStatsApi.UnixTimeStampToDateTimeSeconds(searchResults.data[i].updatedAt);
                            embed.AddField(new EmbedFieldBuilder
                            {
                                Name = $":white_small_square: {searchResults.data[i].displayName}",
                                Value = $"Last Updated: [*{string.Format("{0:MM-dd-yy HH:mm}", humanTime)}*]",
                                IsInline = true
                            });
                        }
                    }
                    else if (searchResults.data.Count == 1)
                    {
                        DateTime humanTime = _rlStatsApi.UnixTimeStampToDateTimeSeconds(searchResults.data[0].updatedAt);
                        embed.AddField(new EmbedFieldBuilder
                        {
                            Name = $":white_small_square: {searchResults.data[0].displayName}",
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

        [Command("rlstats", RunMode = RunMode.Async)]
        [Summary("Get Rocket League Stats. Use this command with set, followed by your steam URL/ID/VanityName to set a default user (rlstats set URL/ID/VanityName")]
        public async Task RlStats([Remainder]string input = "")
        {
            StringBuilder sb = new StringBuilder();
            var rlStats = new RlStat();
            if (!string.IsNullOrEmpty(input))
            {
                if (input.Split(' ').Count() > 1)
                {
                    string arg = input.Split(' ')[1].ToLower().ToString();
                    switch (input.Split(' ')[0].ToString().ToLower())
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
                await SendStats(false);
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

                    SteamModel.Player fromSteam = _steam.getSteamPlayerInfo(rlUserName);
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
            //SteamModel.Player fromSteam = _steam.getSteamPlayerInfo(name);
            if (Uri.IsWellFormedUriString(name, UriKind.RelativeOrAbsolute))
            {
                name = name.TrimEnd('/');
                name = name.Substring(name.LastIndexOf('/') + 1);
                SteamModel.Player fromSteam = _steam.getSteamPlayerInfo(name);
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
            var searchResults = await _rlStatsApi.SearchForPlayer(name);
            if (searchResults.data.Count > 0)
            {
                try
                {
                    EmbedBuilder embed = await RlEmbedApi(searchResults.data[0]);
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

        public async Task GetStats(string name, bool ps)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                EmbedBuilder embed = await rlEmbed(sb, name);
                await _cc.Reply(Context, embed);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            return;
        }

        private static void FormatStatInfo(StringBuilder sb, PropertyInfo rank, SeasonStats rankInfo)
        {
            string rankMoji = string.Empty;
            int matchesPlayed = rankInfo.matchesPlayed;
            int rankPoints = rankInfo.rankPoints;
            string tier = RlStatsApi.Tiers.Where(t => t.tierId == rankInfo.tier).Select(t => t.tierName).FirstOrDefault();
            if (rankInfo.tier >= 1 & rankInfo.tier < 4)
            {
                rankMoji = ":tangerine:";
            }
            else if (rankInfo.tier >= 4 & rankInfo.tier < 7)
            {
                rankMoji = ":full_moon:";
            }
            else if (rankInfo.tier >= 7 & rankInfo.tier < 10)
            {
                rankMoji = ":sunny:";
            }
            else if (rankInfo.tier >= 10 & rankInfo.tier < 13)
            {
                rankMoji = ":fork_knife_plate:";
            }
            else if (rankInfo.tier >= 13 & rankInfo.tier < 16)
            {
                rankMoji = ":large_blue_diamond:";
            }
            else if (rankInfo.tier >= 16 & rankInfo.tier < 19)
            {
                rankMoji = ":purple_heart:";
            }
            else if (rankInfo.tier == 19)
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
            switch (rank.Name)
            {
                case "_onevone":
                    {
                        sb.AppendLine("Duel");
                        break;
                    }
                case "_twovtwo":
                    {
                        sb.AppendLine("Doubles");
                        break;
                    }
                case "_solostandard":
                    {
                        sb.AppendLine("Solo Standard");
                        break;
                    }
                case "_standard":
                    {
                        sb.AppendLine("Standard");
                        break;
                    }
                default:
                    {
                        sb.AppendLine(rank.Name);
                        break;
                    }
            }
            sb.AppendLine($"{rankMoji} [*{tier}(div {rankInfo.division + 1})* [**{rankPoints}**]] Matches played [**{matchesPlayed}**]");
        }

        public async Task SendStats(bool ps)
        {
            try
            {
                string userName = Context.User.Username;
                string channel = Context.Channel.Name;
                StringBuilder sb = new StringBuilder();
                RlStat rlUser = null;
                using (var db = new NinjaBotEntities())
                {
                    rlUser = db.RlStats.FirstOrDefault(r => r.DiscordUserName == userName);
                }
                if (rlUser == null)
                {
                    sb.AppendLine($"Unable to find steam name association for discord user {userName}");
                    await _cc.Reply(Context, sb.ToString());
                    return;
                }
                else
                {
                    string steamUserId = rlUser.SteamID.ToString();
                    SteamModel.Player fromSteam = _steam.getSteamPlayerInfo(steamUserId);

                    if (string.IsNullOrEmpty(fromSteam.steamid))
                    {
                        sb.AppendLine($"Unable to find steam user for steam name/id: {steamUserId}!");
                        sb.AppendLine($"Please try using !rlstats set steamVanityUserNameOrID");
                        await _cc.Reply(Context, sb.ToString());
                        return;
                    }
                    else
                    {

                        //sb.AppendLine($"Stats from: {fullURL}");
                        //EmbedBuilder embed = await rlEmbed(sb, fromSteam);
                        EmbedBuilder embed = await RlEmbedApi(fromSteam);
                        await _cc.Reply(Context, embed);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error looking up stats {ex.Message}");
            }
        }

        private async Task<EmbedBuilder> RlEmbedApi(UserStats userStats)
        {
            long steamId = 0;
            long.TryParse(userStats.uniqueId, out steamId);
            var stats = userStats;
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            embed.Title = $"Stats for [{userStats.displayName}]";
            embed.ThumbnailUrl = userStats.avatar;
            embed.WithColor(new Color(0, 255, 0));
            embed.WithAuthor(new EmbedAuthorBuilder
            {
                Name = $"{Context.User.Username}",
                IconUrl = $"{Context.User.GetAvatarUrl()}"
            });
            var rankings = Enum.GetValues(typeof(RlRankingList));
            var rankingsFromObject = stats.rankedSeasons._5.GetType().GetProperties();
            foreach (var rank in rankingsFromObject)
            {
                var rankInfo = (SeasonStats)stats.rankedSeasons._5.GetType().GetProperty(rank.Name).GetValue(stats.rankedSeasons._5, null);
                FormatStatInfo(sb, rank, rankInfo);
            }
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Season 5",
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
            return embed;
        }

        private async Task<EmbedBuilder> RlEmbedApi(SteamModel.Player fromSteam)
        {
            long steamId = 0;
            long.TryParse(fromSteam.steamid, out steamId);
            var stats = await _rlStatsApi.GetUserStats(steamId);
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
            var rankingsFromObject = stats.rankedSeasons._5.GetType().GetProperties();
            foreach (var rank in rankingsFromObject)
            {
                var rankInfo = (SeasonStats)stats.rankedSeasons._5.GetType().GetProperty(rank.Name).GetValue(stats.rankedSeasons._5, null);
                FormatStatInfo(sb, rank, rankInfo);
            }
            embed.AddField(new EmbedFieldBuilder
            {
                Name = "Season 5",
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
            return embed;
        }

        private async Task<EmbedBuilder> rlEmbed(StringBuilder sb, SteamModel.Player fromSteam)
        {
            RlUserStat getStats = null;
            List<RocketLeagueStats> stats = null;
            var embed = new EmbedBuilder();
            try
            {
                stats = await _rl.getRLStats(fromSteam.steamid);
                getStats = await GetStatsFromDb(fromSteam);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting RL Stats -> [{ex.Message}]");
                embed.Title = $"Error getting stats for {fromSteam.personaname}!";
                embed.Description = $"Sorry, something dun went wrong :(";
                return embed;
            }
            if (stats != null)
            {
                await InsertStats(stats, fromSteam);
            }
            foreach (var stat in stats)
            {
                string rankMoji = ":wavy_dash:";
                if (getStats != null)
                {
                    switch (stat.Title)
                    {
                        case "Doubles 2v2":
                            {
                                var doubles = getStats.Ranked2v2;
                                if (doubles != null)
                                {
                                    if (int.Parse(doubles) < stat.MMR)
                                    {
                                        rankMoji = $":arrow_up: (up **{(stat.MMR - int.Parse(doubles)).ToString()}**)";
                                    }
                                    else if (int.Parse(doubles) > stat.MMR)
                                    {
                                        rankMoji = $":arrow_down: (down **{(int.Parse(doubles) - stat.MMR).ToString()}**)";
                                    }
                                }
                                break;
                            }
                        case "Duel 1v1":
                            {
                                var duel = getStats.RankedDuel;
                                if (duel != null)
                                {
                                    if (int.Parse(duel) < stat.MMR)
                                    {
                                        rankMoji = $":arrow_up: (up **{(stat.MMR - int.Parse(duel)).ToString()}**)";
                                    }
                                    else if (int.Parse(duel) > stat.MMR)
                                    {
                                        rankMoji = $":arrow_down: (down **{(int.Parse(duel) - stat.MMR).ToString()}**)";
                                    }
                                }
                                break;
                            }
                        case "Solo Standard 3v3":
                            {
                                var standard = getStats.RankedSolo;
                                if (standard != null)
                                {
                                    if (int.Parse(standard) < stat.MMR)
                                    {
                                        rankMoji = $":arrow_up: (up **{(stat.MMR - int.Parse(standard)).ToString()}**)";
                                    }
                                    else if (int.Parse(standard) > stat.MMR)
                                    {
                                        rankMoji = $":arrow_down: (down **{(int.Parse(standard) - stat.MMR).ToString()}**)";
                                    }
                                }
                                break;
                            }
                        case "Standard 3v3":
                            {
                                var threev = getStats.Ranked3v3;
                                if (threev != null)
                                {
                                    if (int.Parse(threev) < stat.MMR)
                                    {
                                        rankMoji = $":arrow_up: (up **{(stat.MMR - int.Parse(threev)).ToString()}**)";
                                    }
                                    else if (int.Parse(threev) > stat.MMR)
                                    {
                                        rankMoji = $":arrow_down: (down **{(int.Parse(threev) - stat.MMR).ToString()}**)";
                                    }
                                }
                                break;
                            }
                        case "Unranked":
                            {
                                var unranked = getStats.Unranked;
                                if (unranked != null)
                                {
                                    if (int.Parse(unranked) < stat.MMR)
                                    {
                                        rankMoji = $":arrow_up: (up **{(stat.MMR - int.Parse(unranked)).ToString()}**)";
                                    }
                                    else if (int.Parse(unranked) > stat.MMR)
                                    {
                                        rankMoji = $":arrow_down: (down **{(int.Parse(unranked) - stat.MMR).ToString()}**)";
                                    }
                                }
                                break;
                            }
                    }
                }
                sb.AppendLine($"__{stat.Title}__");
                //sb.AppendLine($"Games Played: **{stat.GamesPlayed}**");
                sb.AppendLine($"Rank: **{stat.Rank}**(Div **{stat.Division}**)");
                sb.AppendLine($"MMR: **{stat.MMR}** {rankMoji}");
                //sb.AppendLine($"**{stat.Percentage}**");
                sb.AppendLine($"");
            }
            var statsUrl = stats.Where(s => s.FromURL != null).Select(s => s.FromURL).FirstOrDefault();
            if (statsUrl != null)
            {
                sb.AppendLine($"Stats from: {statsUrl}");
            }
            embed.WithColor(new Color(0, 71, 171));
            embed.ThumbnailUrl = fromSteam.avatarfull;
            //embed.ThumbnailUrl = Context.User.GetAvatarUrl();
            embed.Title = $"__Rocket League Stats For [**{fromSteam.personaname}**]__";
            embed.Description = sb.ToString();
            return embed;
        }

        private async Task<EmbedBuilder> rlEmbed(StringBuilder sb, string psName)
        {
            List<RocketLeagueStats> stats = null;
            try
            {
                stats = await _rl.getRLStats(psName, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting RL Stats -> [{ex.Message}]");
            }

            foreach (var stat in stats)
            {
                sb.AppendLine($"__{stat.Title}__");
                //sb.AppendLine($"Games Played: **{stat.GamesPlayed}**");
                sb.AppendLine($"Rank: **{stat.Rank}**(Div **{stat.Division}**)");
                sb.AppendLine($"MMR: **{stat.MMR}**");
                //sb.AppendLine($"**{stat.Percentage}**");
                sb.AppendLine($"");
            }
            var statsUrl = stats.Where(s => s.FromURL != null).Select(s => s.FromURL).FirstOrDefault();
            if (statsUrl != null)
            {
                sb.AppendLine($"Stats from: {statsUrl}");
            }
            var embed = new EmbedBuilder();
            embed.WithColor(new Color(0, 71, 171));
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();
            //embed.ThumbnailUrl = Context.User.GetAvatarUrl();
            embed.Title = $"__Rocket League Stats For [**{psName}**]__";
            embed.Description = sb.ToString();
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