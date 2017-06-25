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

namespace NinjaBotCore.Modules.Wow
{
    public class WowCommands : ModuleBase
    {
        private ChannelCheck _cc;
        private WarcraftLogs _logsApi;
        private WowApi _wowApi;
        private DiscordSocketClient _client;

        public WowCommands(WowApi api, ChannelCheck cc, WarcraftLogs logsApi, DiscordSocketClient client)
        {
            if (_cc == null)
            {
                _cc = cc;
            }
            if (_logsApi == null)
            {
                _logsApi = logsApi;
            }
            if (_wowApi == null)
            {
                _wowApi = api;
            }
            if (_client == null)
            {
                _client = client;
            }
        }

        [Command("armory", RunMode = RunMode.Async)]
        [Summary("Gets a character's armory info (WoW)")]
        public async Task GetArmory([Remainder]string args = null)
        {
            string charName = string.Empty;
            string realmName = string.Empty;
            string messageToSend = string.Empty;
            string profileURL = string.Empty;
            string cheevMessage = string.Empty;
            string userName = string.Empty;
            string errorMessage = string.Empty;
            StringBuilder sb = new StringBuilder();
            if (string.IsNullOrEmpty(args))
            {
                var embedError = new EmbedBuilder();
                embedError.Title = $"WoW Armory(NA) Command";
                sb.AppendLine($"Usage examples:");
                sb.AppendLine($":black_small_square: **{Config.Prefix}armory** charactername");
                sb.AppendLine($"\t:black_small_square: Search armory for *charactername* (first in guild, then in the rest of WoW(NA))");
                sb.AppendLine($":black_small_square: **{Config.Prefix}armory** charactername realmname");
                sb.AppendLine($"\t:black_small_square: Search armory for *charactername* on *realmname*");
                embedError.Description = sb.ToString();
                await _cc.Reply(Context, embedError);
                return;
            }
            else
            {
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
                }
            }

            Character armoryInfo;
            GuildChar guildie = null;
            List<FoundChar> chars;
            EmbedBuilder embed = new EmbedBuilder();
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();

            if (string.IsNullOrEmpty(realmName))
            {
                //See if they're a guildie first
                try
                {
                    guildObject = await GetGuildName();
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    Console.WriteLine($"Armory: {errorMessage}");
                }
                if (guildObject.guildName != null && guildObject.realmName != null)
                {
                    guildie = _wowApi.GetCharFromGuild(charName, guildObject.realmName, guildObject.guildName);
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
                }
                else
                {
                    chars = _wowApi.SearchArmory(charName);
                    if (chars != null)
                    {
                        charName = chars[0].charName;
                        realmName = chars[0].realmName;
                        await _cc.Reply(Context, $"If **{charName}** on **{realmName}** is not who were looking for, try {Config.Prefix}wow search {charName} to locate a different character.");
                    }
                    else
                    {
                        await _cc.Reply(Context, $"Unable to find **{charName}**!");
                        return;
                    }
                }
            }
            try
            {
                armoryInfo = _wowApi.GetCharInfo(charName, realmName);
                string powerMessage = GetPowerMessage(armoryInfo);
                if (!string.IsNullOrEmpty(powerMessage))
                {
                    sb.AppendLine(powerMessage);
                }

                string foundAchievements = FindAchievements(armoryInfo);
                if (!string.IsNullOrEmpty(foundAchievements))
                {
                    sb.AppendLine($"__Notable achievements:__");
                    sb.AppendLine(foundAchievements);
                }

                sb.AppendLine($"Armory URL: {armoryInfo.armoryURL}");
                sb.AppendLine($"Last Modified: **{_wowApi.UnixTimeStampToDateTime(armoryInfo.lastModified)}**");

                embed.Description = sb.ToString();
                embed.ThumbnailUrl = armoryInfo.thumbnailURL;
                embed.Title = $"__Armory information for (**{charName}**) on **{realmName}** (Level {armoryInfo.level} {armoryInfo.genderName} {armoryInfo.raceName} {armoryInfo.className} ({armoryInfo.mainSpec}))__";

                switch (armoryInfo.className.ToLower())
                {
                    case "druid":
                        {
                            embed.WithColor(new Color(214, 122, 2));
                            break;
                        }
                    case "death knight":
                        {
                            embed.WithColor(new Color(255, 0, 0));
                            break;
                        }
                    case "demon hunter":
                        {
                            embed.WithColor(new Color(140, 0, 126));
                            break;
                        }
                    case "hunter":
                        {
                            embed.WithColor(new Color(0, 255, 0));
                            break;
                        }
                    case "mage":
                        {
                            embed.WithColor(new Color(0, 250, 255));
                            break;
                        }
                    case "paladin":
                        {
                            embed.WithColor(new Color(255, 0, 220));
                            break;
                        }
                    case "priest":
                        {
                            embed.WithColor(new Color(255, 255, 255));
                            break;
                        }
                    case "rogue":
                        {
                            embed.WithColor(new Color(255, 255, 2));
                            break;
                        }
                    case "shaman":
                        {
                            embed.WithColor(new Color(0, 0, 255));
                            break;
                        }
                    case "warlock":
                        {
                            embed.WithColor(new Color(72, 0, 168));
                            break;
                        }
                    case "warrior":
                        {
                            embed.WithColor(new Color(119, 55, 0));
                            break;
                        }
                }
                await _cc.Reply(Context, embed);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Armory Info Error -> [{ex.Message}]");
                await _cc.Reply(Context, $"Unable to find **{charName}**");
                return;
            }
        }

        [Command("logs", RunMode = RunMode.Async)]
        [Summary("Gets logs from Warcraftlogs")]
        public async Task GetLogs([Remainder] string args = "")
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            string guildName = string.Empty;
            string realmName = string.Empty;
            StringBuilder sb = new StringBuilder();
            List<Reports> guildLogs = new List<Reports>();
            int maxReturn = 2;
            int arrayCount = 0;
            bool privateMessage = false;
            string discordGuildName = string.Empty;
            var guildInfo = Context.Guild;
            var embed = new EmbedBuilder();

            guildObject = await GetGuildName();
            guildName = guildObject.guildName;
            realmName = guildObject.realmName.Replace("'", string.Empty);

            if (guildInfo == null)
            {
                privateMessage = true;
            }
            try
            {
                if (privateMessage)
                {
                    discordGuildName = Context.Channel.Name;
                }
                else
                {
                    discordGuildName = Context.Guild.Name;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Logs Error -> [{ex.Message}]");
                return;
            }
            if (args != null && args.Split(' ')[0].ToLower() == "name")
            {
                try
                {
                    guildLogs = _logsApi.GetReportsFromUser(args.Split(' ')[1]);
                    arrayCount = guildLogs.Count - 1;
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Unable to find logs from **{args.Split(' ')[1]}**");
                    Console.WriteLine($"Erorr getting logs from user -> [{ex.Message}]");
                    await _cc.Reply(Context, sb.ToString());
                    return;
                }
                if (arrayCount > 0)
                {
                    sb.AppendLine();
                    for (int i = 0; i <= maxReturn && i <= guildLogs.Count; i++)
                    {
                        sb.AppendLine($"__**{guildLogs[arrayCount].title}** **/** **{guildLogs[arrayCount].zoneName}**__");
                        sb.AppendLine($"\t:timer: Start time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].start)}**");
                        sb.AppendLine($"\t:stopwatch: End time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].end)}**");
                        sb.AppendLine($"\tLink: **{guildLogs[arrayCount].reportURL}**");
                        sb.AppendLine($"\t:white_check_mark: My WoW: **http://www.checkmywow.com/reports/{guildLogs[arrayCount].id}**");
                        sb.AppendLine();
                        arrayCount--;
                    }
                    Console.WriteLine($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");

                    embed.Title = $":1234: __Logs from **{args.Split(' ')[1]}**__:1234: ";
                    embed.Description = sb.ToString();
                    await _cc.Reply(Context, embed);
                    return;
                }
                else if (arrayCount == 0)
                {
                    sb.AppendLine($"__**{guildLogs[0].title}** **/** **{guildLogs[0].zoneName}**__");
                    sb.AppendLine($"\t:timer: Start time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].start)}**");
                    sb.AppendLine($"\t:stopwatch: End time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].end)}**");
                    sb.AppendLine($"\tLink: **{guildLogs[0].reportURL}**");
                    sb.AppendLine($"\t:white_check_mark: My WoW: **http://www.checkmywow.com/reports/{guildLogs[0].id}**");
                    sb.AppendLine();
                    Console.WriteLine($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234: __Logs for **{guildName}** on **{realmName}**__:1234: ";
                    embed.Description = sb.ToString();
                    await _cc.Reply(Context, embed);
                }
            }
            else
            {
                if (args.Split(',').Count() > 1)
                {
                    if (args.Contains(',') && !string.IsNullOrEmpty(args))
                    {
                        guildName = args.Split(',')[1].ToString().Trim();
                        realmName = args.Split(',')[0].ToString().Trim();
                    }
                    else
                    {
                        sb.AppendLine("Please specify a guild and realm name!");
                        sb.AppendLine($"Example: {Config.Prefix}logs Thunderlord, UR KEY UR CARRY");
                        await _cc.Reply(Context, sb.ToString());
                        return;
                    }
                }
                if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(realmName))
                {
                    sb.AppendLine("Please specify a guild and realm name!");
                    sb.AppendLine($"Example: {Config.Prefix}logs Thunderlord, UR KEY UR CARRY");
                    await _cc.Reply(Context, sb.ToString());
                    return;
                }
                try
                {
                    guildLogs = _logsApi.GetReportsFromGuild(guildName, realmName);
                    arrayCount = guildLogs.Count - 1;
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Unable to find logs for **{guildName}** on **{realmName}**");
                    Console.WriteLine($"{ex.Message}");
                    await _cc.Reply(Context, sb.ToString());
                    await _cc.SetDoneEmoji(Context);
                    return;
                }
                if (arrayCount > 0)
                {
                    sb.AppendLine();
                    for (int i = 0; i <= maxReturn && i <= guildLogs.Count; i++)
                    {
                        sb.AppendLine($"__**{guildLogs[arrayCount].title}** **/** **{guildLogs[arrayCount].zoneName}**__");
                        sb.AppendLine($"\t:timer: Start time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].start)}**");
                        sb.AppendLine($"\t:stopwatch: End time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[arrayCount].end)}**");
                        sb.AppendLine($"\tLink: **{guildLogs[arrayCount].reportURL}**");
                        sb.AppendLine($"\t:white_check_mark: My WoW: **http://www.checkmywow.com/reports/{guildLogs[arrayCount].id}**");
                        sb.AppendLine();
                        arrayCount--;
                    }
                    Console.WriteLine($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234: __Logs for **{guildName}** on **{realmName}**__:1234: ";
                    embed.Description = sb.ToString();
                    await _cc.Reply(Context, embed);
                }
                else if (arrayCount == 0)
                {
                    sb.AppendLine($"__**{guildLogs[0].title}** **/** **{guildLogs[0].zoneName}**__");
                    sb.AppendLine($"\t:timer: Start time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].start)}**");
                    sb.AppendLine($"\t:stopwatch: End time: **{_logsApi.UnixTimeStampToDateTime(guildLogs[0].end)}**");
                    sb.AppendLine($"\tLink: **{guildLogs[0].reportURL}**");
                    sb.AppendLine($"\t:white_check_mark: My WoW: **http://www.checkmywow.com/reports/{guildLogs[0].id}**");
                    sb.AppendLine();
                    Console.WriteLine($"Sending logs to {Context.Channel.Name}, requested by {Context.User.Username}");
                    embed.Title = $":1234: __Logs for **{guildName}** on **{realmName}**__:1234: ";
                    embed.Description = sb.ToString();
                    await _cc.Reply(Context, embed);
                }
                else
                {
                    embed.Title = $"Unable to find logs for {guildName} on {realmName}";
                    embed.Description = $"**{Context.User.Username}**, ensure you've uploaded the logs as attached to **{guildName}** on http://www.warcraftlogs.com \n";
                    embed.Description += $"More information: http://www.wowhead.com/guides/raiding/warcraft-logs";
                    await _cc.Reply(Context, embed);
                }
            }
        }

        [Command("set-guild", RunMode = RunMode.Async)]
        [Summary("Sets a realm/guild association for a Discord server")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task SetGuild([Remainder]string args = "")
        {
            bool privateMessage = false;
            string realmName = string.Empty;
            string guildName = string.Empty;

            if (args.Contains(',') && !string.IsNullOrEmpty(args))
            {
                guildName = args.Split(',')[1].ToString().Trim();
                realmName = args.Split(',')[0].ToString().Trim();
            }
            else
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Please specify a realm and guild name!");
                sb.AppendLine($"Example: {Config.Prefix}set-guild Thunderlord, UR KEY UR CARRY");
                await _cc.Reply(Context, sb.ToString());
                return;
            }
            string discordGuildName = string.Empty;
            var guildInfo = Context.Guild;
            if (guildInfo == null)
            {
                privateMessage = true;
            }
            try
            {
                if (privateMessage)
                {
                    discordGuildName = Context.Channel.Name;
                }
                else
                {
                    discordGuildName = Context.Guild.Name;
                }

                GuildMembers members = null;
                try
                {
                    members = _wowApi.GetGuildMembers(realmName, guildName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error getting guild info -> [{ex.Message}]");
                }

                if (members != null)
                {
                    guildName = members.name;
                    realmName = members.realm;
                    await SetGuildAssociation(guildName, realmName);
                    await GetGuild();
                }
                else
                {
                    await _cc.Reply(Context, $"Unable to associate guild/realm (**{guildName}**/**{realmName}**) to **{discordGuildName}** (typo?)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Set-Guild error: {ex.Message}");
            }
        }

        [Command("get-guild", RunMode = RunMode.Async)]
        [Summary("Report Discord Server -> Guild Association")]
        public async Task GetGuild([Remainder] string args = "")
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            string title = string.Empty;
            GuildMembers members = null;
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
            NinjaObjects.GuildObject guildObject = await GetGuildName();

            title = $"Guild association for **{discordGuildName}**";

            embed.Title = title;
            embed.ThumbnailUrl = thumbUrl;
            if (guildObject.guildName != null || members != null)
            {
                try
                {
                    members = _wowApi.GetGuildMembers(guildObject.realmName, guildObject.guildName);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Get-Guild Error getting guild info: [{ex.Message}]");
                }
                string guildName = string.Empty;
                string guildRealm = string.Empty;
                string faction = string.Empty;
                string battlegroup = members.battlegroup;
                int achievementPoints = members.achievementPoints;
                switch (members.side)
                {
                    case 0:
                        {
                            faction = "Alliance";
                            embed.WithColor(new Color(0, 0, 255));
                            break;
                        }
                    case 1:
                        {
                            faction = "Horde";
                            embed.WithColor(new Color(255, 0, 0));
                            break;
                        }
                }
                guildName = members.name;
                guildRealm = members.realm;
                sb.AppendLine($"Guild Name: **{guildName}**");
                sb.AppendLine($"Realm Name: **{guildRealm}**");
                sb.AppendLine($"Members: **{members.members.Count().ToString()}**");
                sb.AppendLine($"Battlegroup: **{battlegroup}**");
                sb.AppendLine($"Faction: **{faction}**");
                sb.AppendLine($"Achievement Points: **{achievementPoints.ToString()}**");
            }
            else
            {
                sb.AppendLine($"No guild association found for **{discordGuildName}**!");
                sb.AppendLine($"Please use !set-guild realmName guild name to associate a guild with **{discordGuildName}**");
            }
            embed.Description = sb.ToString();
            await _cc.Reply(Context, embed);
        }

        [Command("wow", RunMode = RunMode.Async)]
        [Summary("Use this combined with rankings (gets guild rank from WoWProgress), auctions (gets auctions for default realm ot realm specified), or search (search for a character!)")]
        public async Task GetRanking([Remainder] string args = null)
        {
            StringBuilder sb = new StringBuilder();
            bool commandProcessed = false;
            if (args != null)
            {
                if (args.Split(' ').Count() > 0)
                {
                    string commandArgs = string.Empty;
                    for (int i = 0; i < args.Split(' ').Count(); i++)
                    {
                        if (i > 0)
                        {
                            sb.Append($"{args.Split(' ')[i]} ");
                        }
                    }
                    commandArgs = sb.ToString().Trim();
                    switch (args.Split(' ')[0].ToLower())
                    {
                        case "rankings":
                            {
                                await GetWowRankings(commandArgs);
                                commandProcessed = true;
                                break;
                            }
                        case "auctions":
                            {
                                await GetAuctionItems(commandArgs);
                                commandProcessed = true;
                                break;
                            }
                        case "search":
                            {
                                await SearchWowChars(commandArgs);
                                commandProcessed = true;
                                break;
                            }
                    }
                }
            }
            if (!commandProcessed)
            {
                sb.Clear();
                sb.AppendLine($":black_medium_small_square: **rankings**");
                sb.AppendLine($"\t :black_small_square: Get rankings for the guild associated with this Discord server from http://www.wowprogress.com");
                sb.AppendLine($"\t :black_small_square: Alternatively, you can provide a guild and realm in this format: {Config.Prefix}wow rankings *realmname*, *guildname*");
                sb.AppendLine($":black_medium_small_square: **auctions**");
                sb.AppendLine($"\t :black_small_square: Get a list of the current auctions (most used raiding/crafting items) for the realm associated with this Discord server");
                sb.AppendLine($"\t :black_small_square: Alternatively, you can provide a realm in this format: {Config.Prefix}wow auctions *realmname*");
                sb.AppendLine($":black_medium_small_square: **search**");
                sb.AppendLine($"\t :black_small_square: Search the WoW website for a character name to find out the associated level and realm");
                var embed = new EmbedBuilder();
                embed.Title = $"{Config.Prefix}wow Command Usage";
                embed.Description = sb.ToString();
                await _cc.Reply(Context, embed);
            }
        }

        private async Task<GuildChar> GetCharFromArgs(string args, ICommandContext context)
        {
            string charName = string.Empty;
            string realmName = string.Empty;
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
            }
            if (string.IsNullOrEmpty(realmName))
            {
                //See if they're a guildie first
                try
                {
                    guildObject = await GetGuildName();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error looking up character: {ex.Message}");
                }
                if (guildObject.guildName != null && guildObject.realmName != null)
                {
                    guildie = _wowApi.GetCharFromGuild(charName, guildObject.realmName, guildObject.guildName);
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
            charInfo.charName = charName;
            charInfo.realmName = realmName;
            return charInfo;
        }

        [Command("gearlist")]
        [Summary("Get a WoW character's gear list")]
        public async Task GetGearList([Remainder] string args)
        {
            //Add error to response if there is one
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            try
            {
                var charInfo = await GetCharFromArgs(args, Context);
                Character armoryInfo = new Character();
                if (!string.IsNullOrEmpty(charInfo.charName) && !string.IsNullOrEmpty(charInfo.realmName))
                {
                    armoryInfo = _wowApi.GetCharInfo(charInfo.charName, charInfo.realmName);
                    embed.Title = $"Gear List For {charInfo.charName} on {charInfo.realmName}";
                    embed.ThumbnailUrl = armoryInfo.profilePicURL;
                    if (armoryInfo.items.head != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Head ({armoryInfo.items.head.itemLevel})",
                            Value = $"[{armoryInfo.items.head.name}](http://www.wowhead.com/item={armoryInfo.items.head.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.hands != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Hands ({armoryInfo.items.hands.itemLevel})",
                            Value = $"[{armoryInfo.items.hands.name}](http://www.wowhead.com/item={armoryInfo.items.hands.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.neck != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Neck ({armoryInfo.items.neck.itemLevel})",
                            Value = $"[{armoryInfo.items.neck.name}](http://www.wowhead.com/item={armoryInfo.items.neck.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.waist != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Waist ({armoryInfo.items.waist.itemLevel})",
                            Value = $"[{armoryInfo.items.waist.name}](http://www.wowhead.com/item={armoryInfo.items.waist.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.shoulder != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Shoulders ({armoryInfo.items.shoulder.itemLevel})",
                            Value = $"[{armoryInfo.items.shoulder.name}](http://www.wowhead.com/item={armoryInfo.items.shoulder.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.legs != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Pants ({armoryInfo.items.legs.itemLevel})",
                            Value = $"[{armoryInfo.items.legs.name}](http://www.wowhead.com/item={armoryInfo.items.legs.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.back != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Back ({armoryInfo.items.back.itemLevel})",
                            Value = $"[{armoryInfo.items.back.name}](http://www.wowhead.com/item={armoryInfo.items.back.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.feet != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Feet ({armoryInfo.items.feet.itemLevel})",
                            Value = $"[{armoryInfo.items.feet.name}](http://www.wowhead.com/item={armoryInfo.items.feet.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.chest != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Chest ({armoryInfo.items.chest.itemLevel})",
                            Value = $"[{armoryInfo.items.chest.name}](http://www.wowhead.com/item={armoryInfo.items.chest.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.finger1 != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Ring ({armoryInfo.items.finger1.itemLevel})",
                            Value = $"[{armoryInfo.items.finger1.name}](http://www.wowhead.com/item={armoryInfo.items.finger1.id})",
                            IsInline = true
                        });
                    }

                    if (armoryInfo.items.wrist != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Wrists ({armoryInfo.items.wrist.itemLevel})",
                            Value = $"[{armoryInfo.items.wrist.name}](http://www.wowhead.com/item={armoryInfo.items.wrist.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.trinket1 != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Trinket ({armoryInfo.items.trinket1.itemLevel})",
                            Value = $"[{armoryInfo.items.trinket1.name}](http://www.wowhead.com/item={armoryInfo.items.trinket1.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.finger2 != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Ring ({armoryInfo.items.finger2.itemLevel})",
                            Value = $"[{armoryInfo.items.finger2.name}](http://www.wowhead.com/item={armoryInfo.items.finger2.id})",
                            IsInline = true
                        });
                    }

                    if (armoryInfo.items.trinket2 != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"Trinket ({armoryInfo.items.trinket2.itemLevel})",
                            Value = $"[{armoryInfo.items.trinket2.name}](http://www.wowhead.com/item={armoryInfo.items.trinket2.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.mainHand != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"MainHand ({armoryInfo.items.mainHand.itemLevel})",
                            Value = $"[{armoryInfo.items.mainHand.name}](http://www.wowhead.com/item={armoryInfo.items.mainHand.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.offHand != null)
                    {
                        embed.Fields.Add(new EmbedFieldBuilder
                        {
                            Name = $"OffHand ({armoryInfo.items.offHand.itemLevel})",
                            Value = $"[{armoryInfo.items.offHand.name}](http://www.wowhead.com/item={armoryInfo.items.offHand.id})",
                            IsInline = true
                        });
                    }
                    if (armoryInfo.items.averageItemLevel < 850)
                    {
                        embed.WithColor(new Color(0, 255, 0));
                    }
                    else if (armoryInfo.items.averageItemLevel > 850 && armoryInfo.items.averageItemLevel < 885)
                    {
                        embed.WithColor(new Color(0, 0, 255));
                    }
                    else
                    {
                        embed.WithColor(new Color(148, 0, 211));
                    }
                    sb.AppendLine($"Average ilvl ({armoryInfo.items.averageItemLevel}) Equipped ilvl ({armoryInfo.items.averageItemLevelEquipped})");
                    embed.Footer = new EmbedFooterBuilder { Text = sb.ToString() };
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Error getting gear list, sorry!");
                embed.Description = sb.ToString();
                System.Console.WriteLine($"Error getting gear list for {ex.Message}");
            }
            await _cc.Reply(Context, embed);
        }

        [Command("top10", RunMode = RunMode.Async)]
        [Summary("Get the top 10 dps or hps for the latest raid in World of Warcraft (via warcraftlogs.com)")]
        public async Task GetTop10([Remainder] string args = null)
        {
            StringBuilder sb = new StringBuilder();
            string fightName = string.Empty;
            string guildOnly = string.Empty;
            string difficulty = string.Empty;
            string metric = string.Empty;
            int encounterID = 0;
            var fightList = WarcraftLogs.Zones.Where(z => z.id == 11).Select(z => z.encounters).FirstOrDefault();
            var embed = new EmbedBuilder();

            //Get Guild Information for Discord Server, start embed            
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
            NinjaObjects.GuildObject guildObject = await GetGuildName();

            string realmName = guildObject.realmName.Replace("'", string.Empty);
            string guildName = guildObject.guildName;
            //Argument logic
            if (args == null || args.Split(',')[0] == "help")
            {
                sb.AppendLine($"**{Config.Prefix}top10** fightName(or ID from {Config.Prefix}top10 list) guild(type guild to get guild only results, all for all guilds) metric(dps(default), or hps) difficulty(lfr, flex, normal, heroic(default), or mythic) ");
                sb.AppendLine();
                sb.AppendLine($"**{Config.Prefix}top10** list");
                sb.AppendLine($"Get a list of all encounters and shortcut IDs");
                sb.AppendLine();
                sb.AppendLine($"**{Config.Prefix}top10** 1");
                sb.AppendLine($"The above command would get all top 10 **dps** results for **Skorpyron** on **{realmName}**.");
                sb.AppendLine();
                sb.AppendLine($"**{Config.Prefix}top10** 1 guild");
                sb.AppendLine($"The above command would get the top 10 **dps** results for **Skorpyron** on **{realmName}** for **{guildName}**.");
                sb.AppendLine();
                sb.AppendLine($"**{Config.Prefix}top10** 1 guild hps");
                sb.AppendLine($"The above command would get the top 10 **hps** results for **Skorpyron** on **{realmName}** for **{guildName}**.");
                sb.AppendLine();
                sb.AppendLine($"**{Config.Prefix}top10** 1 all hps");
                sb.AppendLine($"The above command would get all top 10 **hps** results for **Skorpyron** on **{realmName}**.");
                sb.AppendLine();
                sb.AppendLine($"**{Config.Prefix}top10** 1 guild dps mythic");
                sb.AppendLine($"The above command would get the top 10 **dps** results for **Skorpyron** on **{realmName}** for **{guildName}** on **mythic** difficulty.");
                embed.Title = $"{Context.User.Username}, here are some examples for **{Config.Prefix}top10**";
                embed.Description = sb.ToString();
                await _cc.Reply(Context, embed);
                return;
            }
            else
            {
                if (args.Split(' ')[0].ToLower() == "list")
                {
                    //list fights here
                    if (fightList != null)
                    {
                        embed.Title = "__Fight names for **The Nighthold**__";
                        int j = 1;
                        foreach (var fight in fightList)
                        {
                            sb.AppendLine($"[**{j}**] {fight.name}");
                            j++;
                        }
                        embed.Description = sb.ToString();
                        await _cc.Reply(Context, embed);
                    }
                    return;
                }
                difficulty = "heroic";
                int argCount = args.Split(',').Count();
                string[] splitArgs = args.Split(',');
                switch (argCount)
                {
                    //Just name
                    case 1:
                        {
                            fightName = splitArgs[0].Trim();
                            break;
                        }
                    //Name + metric
                    case 2:
                        {
                            fightName = splitArgs[0].Trim();
                            metric = splitArgs[1].Trim();
                            break;
                        }
                    case 3:
                        {
                            fightName = splitArgs[0].Trim();
                            guildOnly = splitArgs[1].Trim();
                            metric = splitArgs[2].Trim();
                            break;
                        }
                    case 4:
                        {
                            fightName = splitArgs[0].Trim();
                            guildOnly = splitArgs[1].Trim();
                            metric = splitArgs[2].Trim();
                            difficulty = splitArgs[3].Trim();
                            break;
                        }
                }
                //Difficulty logic
                int difficultyID = 4;
                switch (difficulty.ToLower())
                {
                    case "lfr":
                        {
                            difficultyID = 1;
                            break;
                        }
                    case "flex":
                        {
                            difficultyID = 2;
                            break;
                        }
                    case "normal":
                        {
                            difficultyID = 3;
                            break;
                        }
                    case "heroic":
                        {
                            difficultyID = 4;
                            break;
                        }
                    case "mythic":
                        {
                            difficultyID = 5;
                            break;
                        }
                }
                //End difficulty logic
                //Get the list of fights that pertain to the specific zone id. 11 == Nighthold (True)
                //Begin fight logic                
                WarcraftlogRankings.RankingObject l = new WarcraftlogRankings.RankingObject();
                if (fightName.Length <= 2)
                {
                    switch (fightName)
                    {
                        case "1":
                            {
                                encounterID = fightList[0].id;
                                break;
                            }
                        case "2":
                            {
                                encounterID = fightList[1].id;
                                break;
                            }
                        case "3":
                            {
                                encounterID = fightList[2].id;
                                break;
                            }
                        case "4":
                            {
                                encounterID = fightList[3].id;
                                break;
                            }
                        case "5":
                            {
                                encounterID = fightList[4].id;
                                break;
                            }
                        case "6":
                            {
                                encounterID = fightList[5].id;
                                break;
                            }
                        case "7":
                            {
                                encounterID = fightList[6].id;
                                break;
                            }
                        case "8":
                            {
                                encounterID = fightList[7].id;
                                break;
                            }
                        case "9":
                            {
                                encounterID = fightList[8].id;
                                break;
                            }
                        case "10":
                            {
                                encounterID = fightList[9].id;
                                break;
                            }
                    }
                }
                else
                {
                    encounterID = GetEncounterID(fightName);
                }
                //End fight logic               
                //Begin metric set
                string metricEmoji = string.Empty;
                if (string.IsNullOrEmpty(metric))
                {
                    metric = "dps";
                }
                switch (metric.ToLower())
                {
                    case "hps":
                        {
                            embed.WithColor(new Color(0, 255, 0));
                            metricEmoji = ":green_heart:";
                            break;
                        }
                    case "dps":
                        {
                            embed.WithColor(new Color(255, 0, 0));
                            metricEmoji = ":dagger: ";
                            break;
                        }
                    default:
                        {
                            embed.WithColor(new Color(255, 0, 0));
                            metricEmoji = ":dagger: ";
                            metric = "dps";
                            break;
                        }
                }
                //End metric set
                if (string.IsNullOrEmpty(fightName))
                {
                    sb.AppendLine($"{Context.User.Username}, please specify a fight name!");
                    sb.AppendLine($"**Example:** !top10 Skorpyron");
                    sb.AppendLine($"**Encounter Lists:** !top10 list");
                    await _cc.Reply(Context, sb.ToString());
                    return;
                }
                if (!(string.IsNullOrEmpty(guildOnly) || guildOnly.ToLower() != "guild"))
                {
                    l = _logsApi.GetRankingsByEncounterGuild(encounterID, realmName, guildObject.guildName, metric, difficultyID);
                }
                else
                {
                    l = _logsApi.GetRankingsByEncounter(encounterID, realmName, metric, difficultyID);
                }
                string fightNameFromEncounterID = fightList.Where(f => f.id == encounterID).Select(f => f.name).FirstOrDefault();
                var top10 = l.rankings.OrderByDescending(a => a.total).Take(10);
                string difficultyName = string.Empty;
                switch (difficultyID)
                {
                    case 1:
                        {
                            difficultyName = "LFR";
                            break;
                        }
                    case 2:
                        {
                            difficultyName = "Flex";
                            break;
                        }
                    case 3:
                        {
                            difficultyName = "Normal";
                            break;
                        }
                    case 4:
                        {
                            difficultyName = "Heroic";
                            break;
                        }
                    case 5:
                        {
                            difficultyName = "Mythic";
                            break;
                        }
                }
                embed.Title = $"__Top 10 for fight [**{fightNameFromEncounterID}** (Metric [**{metric.ToUpper()}**] Difficulty [**{difficultyName}**]) Realm [**{guildObject.realmName}**]]__";
                int i = 1;
                foreach (var rank in top10)
                {
                    var classInfo = WarcraftLogs.CharClasses.Where(c => c.id == rank._class).FirstOrDefault();
                    sb.AppendLine($"**{i}** [**{rank.name}**](http://us.battle.net/wow/en/character/{rank.server}/{rank.name}/advanced) ilvl **{rank.itemLevel}** {classInfo.name} from *{rank.guild}*");
                    sb.AppendLine($"\t{metricEmoji}[**{rank.total.ToString("###,###")}** {metric.ToLower()}]");
                    i++;
                }
                sb.AppendLine($"Data gathered from **https://www.warcraftlogs.com**");
                embed.Description = sb.ToString();
                embed.ThumbnailUrl = thumbUrl;
                try
                {
                    await _cc.Reply(Context, embed);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            }
        }

        [Command("realm-info", RunMode = RunMode.Async)]
        [Summary("Return WoW realm information")]
        public async Task GetRealmInfo([Remainder] string args)
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            var foundRealm = WowApi.RealmInfo.realms.Where(r => r.name.ToLower().Contains(args.ToLower())).FirstOrDefault();
            if (foundRealm != null)
            {
                embed.Title = $"Realm Information for {foundRealm.name}!";
                sb.AppendLine($":black_small_square: Battlegroup: **{foundRealm.battlegroup}**");
                sb.AppendLine($":black_small_square: Type: **{foundRealm.type}**");
                sb.AppendLine($":black_small_square: Locale: **{foundRealm.locale}**");
                sb.AppendLine($":black_small_square: Population: **{foundRealm.population}**");
                sb.AppendLine($":black_small_square: Status: **{foundRealm.status}**");
                sb.AppendLine($":black_small_square: TimeZone: **{foundRealm.timezone}**");
                sb.AppendLine($":black_small_square: Queue: **{foundRealm.queue}**");
                sb.AppendLine($":black_small_square: Connected Realms:");
                foreach (var realm in foundRealm.connected_realms)
                {
                    sb.AppendLine($"\t :black_small_square: **{realm}**");
                }
            }
            if (foundRealm.status)
            {
                embed.WithColor(new Color(0, 255, 0));
            }
            else
            {
                embed.WithColor(new Color(255, 0, 0));
            }
            embed.Description = sb.ToString();
            await _cc.Reply(Context, embed);
        }

        private async Task SearchWowChars(string args)
        {
            if (args.Split(' ').Count() > 1)
            {
                await _cc.SetDoneEmoji(Context);
                await _cc.Reply(Context, $"Please specify only a character name for the search!");
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
            await _cc.Reply(Context, embed);
        }

        private async Task GetAuctionItems(string args)
        {
            try
            {
                NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
                string realmName = string.Empty;
                if (string.IsNullOrEmpty(args))
                {
                    guildObject = await GetGuildName();
                    realmName = guildObject.realmName;
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
                    await _cc.Reply(Context, $"Unable to find a guild/realm association!\nTry {Config.Prefix}wow auctions realmName guildName");
                    return;
                }
                Console.WriteLine($"Looking up auctions for realm {realmName.ToUpper()}");
                List<WowAuctions> auctions = await _wowApi.GetAuctionsByRealm(realmName.ToLower());
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
                await _cc.Reply(Context, embed);
                using (var db = new NinjaBotEntities())
                {
                    foreach (var item in auctionList)
                    {
                        Console.WriteLine(item.ItemID);
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
                Console.WriteLine($"Auction error: [{ex.Message}]");
                await _cc.Reply(Context, "Error getting auctions :(");
            }
        }

        private List<AuctionList> GetAuctionItemIDs()
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

        private long GetLowestBuyoutPrice(IEnumerable<WowAuctions> auctions)
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
                Console.WriteLine($"Get Lowest Buyout Error -> [{ex.Message}]");
            }
            return lowestBuyoutPrice;
        }

        private long GetHighestBuyoutPrice(IEnumerable<WowAuctions> auctions)
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
                Console.WriteLine($"Get Highest Buyout Error -> [{ex.Message}]");
            }
            return highestBuyoutPrice;
        }

        private long GetAveragePrice(IEnumerable<WowAuctions> auctions)
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
                Console.WriteLine($"Get Average Buyout Error -> [{ex.Message}]");
            }
            return averageBuyoutPrice;
        }

        private async Task GetWowRankings(string args = "")
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            string guildName = string.Empty;
            string realmName = string.Empty;
            if (string.IsNullOrEmpty(args))
            {
                guildObject = await GetGuildName();
                guildName = guildObject.guildName;
                realmName = guildObject.realmName;
            }
            else
            {
                if (args.Contains(','))
                {
                    guildName = args.Split(',')[1].ToString().Trim();
                    realmName = args.Split(',')[0].ToString().Trim();
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    var embed = new EmbedBuilder();
                    embed.WithColor(new Color(255, 0, 0));
                    embed.Title = $"Unable to find a guild/realm association!\nTry {Config.Prefix}wow rankings Realm Name, Guild Name";
                    sb.AppendLine($"Command syntax: {Config.Prefix}wow rankings realm name, guild name");
                    sb.AppendLine($"Command example: {Config.Prefix}wow rankings azgalor, carebears");
                    embed.Description = sb.ToString();
                    await _cc.Reply(Context, embed);
                    return;
                }
            }
            if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(realmName))
            {
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 0, 0));
                embed.Title = $"Unable to find a guild/realm association!\nTry {Config.Prefix}wow rankings Realm Name, Guild Name";
                sb.AppendLine($"Command syntax: {Config.Prefix}wow rankings realm name, guild name");
                sb.AppendLine($"Command example: {Config.Prefix}wow rankings azgalor, carebears");
                embed.Description = sb.ToString();
                await _cc.Reply(Context, embed);
                return;
            }
            try
            {
                var guildMembers = _wowApi.GetGuildMembers(realmName, guildName);
                int memberCount = 0;
                if (guildMembers != null)
                {
                    guildName = guildMembers.name;
                    realmName = guildMembers.realm;
                    memberCount = guildMembers.members.Count();
                }
                var wowProgressApi = new WowProgress();
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 255, 0));
                var ranking = wowProgressApi.getGuildRank(guildName, realmName);
                var realmObject = wowProgressApi.getRealmObject(realmName, wowProgressApi._links);
                var topGuilds = realmObject.OrderBy(r => r.realm_rank).Take(3);
                var guild = realmObject.Where(r => r.name.ToLower() == guildName.ToLower()).FirstOrDefault();
                int guildRank = guild.realm_rank;
                var surroundingGuilds = realmObject.Where(r => r.realm_rank > (guild.realm_rank - 2) && r.realm_rank < (guild.realm_rank + 2));

                embed.Title = $"__:straight_ruler:Guild ranking for **{guildName}** [**{memberCount}** members] (Score: **{ranking.score}**):straight_ruler:__";
                sb.AppendLine($"Realm rank: **{ranking.realm_rank}** **|** World rank: **{ranking.world_rank}** **|** Area rank: **{ranking.area_rank}**");
                sb.AppendLine();
                sb.AppendLine($"__Where **{guildName}** fits in on **{realmName}**__");
                foreach (var singleGuild in surroundingGuilds)
                {
                    sb.AppendLine($"\t(**{singleGuild.realm_rank}**) **{singleGuild.name}** **|** World rank: **{singleGuild.world_rank}**");
                }
                sb.AppendLine();
                sb.AppendLine($"__:top:Top 3 guilds on **{realmName}**:top:__");
                foreach (var topGuild in topGuilds)
                {
                    sb.AppendLine($"\t(**{topGuild.realm_rank}**) **{topGuild.name}** **|** World Rank: **{topGuild.world_rank}**");
                }
                sb.AppendLine();
                sb.AppendLine("Ranking data gathered via **WoWProgress.com**");
                embed.WithUrl($"{guild.url}");
                embed.Description = sb.ToString();

                await _cc.Reply(Context, embed);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex.Message} {ex.InnerException} {ex.Data}{ex.Source}{ex.StackTrace}");
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 0, 0));
                embed.Title = $":frowning: Sorry, {Context.User.Username}, something went wrong! Perhaps check the guild's home realm.:frowning: ";
                sb.AppendLine($"Command syntax: {Config.Prefix}wow rankings realm name, guild name");
                sb.AppendLine($"Command example: {Config.Prefix}wow rankings azgalor, carebears");
                embed.Description = sb.ToString();
                await _cc.Reply(Context, embed);
            }
        }

        private async Task<NinjaObjects.GuildObject> GetGuildName()
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            try
            {
                var guildInfo = Context.Guild;
                if (guildInfo == null)
                {
                    guildObject = await GetGuildAssociation(Context.User.Username);
                }
                else
                {
                    guildObject = await GetGuildAssociation(Context.Guild.Name);
                }
                Console.WriteLine($"getGuildName: {Context.Channel.Name} : {guildObject.guildName} -> {guildObject.realmName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"getGuildName: {ex.Message}");
            }
            return guildObject;
        }

        private async Task<NinjaObjects.GuildObject> GetGuildAssociation(string discordGuildName)
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            using (var db = new NinjaBotEntities())
            {
                var foundGuild = db.WowGuildAssociations.FirstOrDefault(g => g.ServerName == discordGuildName);
                if (foundGuild != null)
                {
                    guildObject.guildName = foundGuild.WowGuild;
                    guildObject.realmName = foundGuild.WowRealm;
                }
            }
            return guildObject;
        }

        private async Task SetGuildAssociation(string wowGuildName, string realmName)
        {
            try
            {
                var guildInfo = Context.Guild;
                string guildName = string.Empty;
                ulong guildId;
                if (guildInfo == null)
                {
                    guildName = Context.User.Username;
                    guildId = Context.User.Id;
                }
                else
                {
                    guildName = guildInfo.Name;
                    guildId = guildInfo.Id;
                }
                using (var db = new NinjaBotEntities())
                {
                    var foundGuild = db.WowGuildAssociations.FirstOrDefault(g => g.ServerName == guildName);
                    if (foundGuild == null)
                    {
                        WowGuildAssociations newGuild = new WowGuildAssociations
                        {
                            ServerId = (long)guildId,
                            ServerName = guildName,
                            WowGuild = wowGuildName,
                            WowRealm = realmName,
                            SetBy = Context.User.Username,
                            SetById = (long)Context.User.Id,
                            TimeSet = DateTime.Now
                        };
                        db.WowGuildAssociations.Add(newGuild);
                    }
                    else
                    {
                        foundGuild.ServerId = (long)guildId;
                        foundGuild.WowGuild = wowGuildName;
                        foundGuild.WowRealm = realmName;
                        foundGuild.SetBy = Context.User.Username;
                        foundGuild.SetById = (long)Context.User.Id;
                        foundGuild.TimeSet = DateTime.Now;
                    }
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting guild assoction for {Context.Guild.Name} to {wowGuildName}-{realmName} [{ex.Message}]");
            }
        }

        private string FindAchievements(Character armoryInfo)
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
                            cheevMessage.AppendLine($":white_check_mark: {matchedCheeve.title}");
                        }
                    }
                }
            }
            return cheevMessage.ToString();
        }

        private string GetPowerMessage(Character armoryInfo)
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

        private int GetEncounterID(string encounterName, string zoneName = "The Nighthold")
        {
            int encounterID = 0;
            var zone = WarcraftLogs.Zones.Where(z => z.name.ToLower() == zoneName.ToLower()).FirstOrDefault();
            if (zone != null)
            {
                encounterID = zone.encounters.Where(e => e.name.ToLower() == encounterName.ToLower()).FirstOrDefault().id;
            }
            return encounterID;
        }
    }
}