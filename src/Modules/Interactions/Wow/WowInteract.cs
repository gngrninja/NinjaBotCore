using Discord;
using Discord.Interactions;
using NinjaBotCore.Attributes;
using System;
using System.Threading.Tasks;
using NinjaBotCore.Services;
using System.Linq;
using System.Text;
using Discord.Net;
using Discord.WebSocket;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Common;
using System.IO;
using System.Threading;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Models.Wow.Housing;
using NinjaBotCore.Database;
using System.Collections.Generic;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Net.Http;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    // Interaction modules must be public and inherit from an IInteractionModuleBase
    public class WowInteract : NinjaBotBaseModule
    {
        // Dependencies can be accessed through Property injection, public properties with public setters will be set by the service provider
        public InteractionService Commands { get; set; }
        private InteractionHandler _handler;
        private WarcraftLogs _logsApi;
        private WarcraftLogsV2Client _logsApiV2;
        private WowApi _wowApi;
        private DiscordShardedClient _client;
        private RaiderIOApi _rioApi;
        private readonly IConfigurationRoot _config;
        private readonly ILogger _logger;
        private WowUtilities _wowUtils;
        private WowCacheService _wowCache;
        private WowStaticDataService _wowStaticData;
        private WowTokenService _tokenService;

        // Pattern #3: Constructor injection instead of service locator
        public WowInteract(
            IServiceScopeFactory scopeFactory,
            InteractionHandler handler,
            ILogger<WowInteract> logger,
            WarcraftLogs logsApi,
            WarcraftLogsV2Client logsApiV2,
            WowApi wowApi,
            RaiderIOApi rioApi,
            DiscordShardedClient client,
            IConfigurationRoot config,
            WowUtilities wowUtils,
            WowCacheService wowCache,
            WowStaticDataService wowStaticData,
            WowTokenService tokenService)
            : base(scopeFactory)
        {
            _handler = handler;
            _logger = logger;
            _logsApi = logsApi;
            _logsApiV2 = logsApiV2;
            _wowApi = wowApi;
            _rioApi = rioApi;
            _client = client;
            _config = config;
            _wowUtils = wowUtils;
            _wowCache = wowCache;
            _wowStaticData = wowStaticData;
            _tokenService = tokenService;
        }

        [SlashCommand("ksm", "Check a character for the Keystone Master achievement")]
        public async Task CheckKsm(string args = null)
        {
            var charInfo = await _wowUtils.GetCharFromArgs(args, Context);
            var sb = new StringBuilder();
            var embed = new EmbedBuilder();
            bool ksm = false;
            embed.Title = "Keystone Master Achievement Check";
            if (!string.IsNullOrEmpty(charInfo.charName))
            {
                Character charAchievements = null;
                if (!string.IsNullOrEmpty(charInfo.regionName))
                {
                    charAchievements = await _wowApi.GetCharInfoAsync(charInfo.charName, charInfo.realmName, charInfo.regionName);
                }
                else
                {
                    charAchievements = await _wowApi.GetCharInfoAsync(charInfo.charName, charInfo.realmName);
                }
                if (charAchievements != null)
                {
                    foreach (var cheeve in charAchievements.achievements.achievementsCompleted)
                    {
                        if (cheeve == 11162)
                        {
                            ksm = true;
                        }
                    }
                }
                if (!ksm)
                {
                    sb.AppendLine($"**{charAchievements.name}** from **{charAchievements.realm}** does not have the Keystone Master achievement! :(");
                    embed.WithColor(new Color(255, 0, 0));
                }
                else
                {
                    sb.AppendLine($"**{charAchievements.name}** from **{charAchievements.realm}** has the Keystone Master achievement! :)");
                    embed.WithColor(new Color(0, 255, 0));
                }
                embed.ThumbnailUrl = charAchievements.thumbnailURL;
            }
            else
            {
                sb.AppendLine($"Sorry, unable to find that character!");
            }
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("wow", "Use this combined with rankings (gets guild rank from WoWProgress")]
        public async Task GetRanking(string args = null)
        {
            NinjaObjects.GuildObject guildObject = new NinjaObjects.GuildObject();
            string guildName = string.Empty;
            string realmName = string.Empty;
            string regionName = "us";
            await DeferAsync(ephemeral: true);
            if (string.IsNullOrEmpty(args))
            {
                guildObject = await _wowUtils.GetGuildName(Context);
                guildName = guildObject.guildName;
                realmName = guildObject.realmName;
                regionName = guildObject.regionName;
            }
            else
            {
                if (args.Contains(','))
                {
                    switch (args.Split(',').Count())
                    {
                        case 2:
                            {
                                realmName = args.Split(',')[0].ToString().Trim();
                                guildName = args.Split(',')[1].ToString().Trim();
                                break;
                            }
                        case 3:
                            {
                                realmName = args.Split(',')[0].ToString().Trim();
                                guildName = args.Split(',')[1].ToString().Trim();
                                regionName = args.Split(',')[2].ToString().Trim();
                                break;
                            }
                    }
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    var embed = new EmbedBuilder();
                    embed.WithColor(new Color(255, 0, 0));
                    embed.Title = $"Unable to find a guild/realm association!\nTry /wow rankings Realm Name, Guild Name";
                    sb.AppendLine($"Command syntax: /wow rankings realm name, guild name");
                    sb.AppendLine($"Command example: /wow rankings azgalor, carebears");
                    embed.Description = sb.ToString();
                    await FollowupAsync(embed: embed.Build(), ephemeral: true);
                    return;
                }
            }
            if (string.IsNullOrEmpty(guildName) || string.IsNullOrEmpty(realmName))
            {
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 0, 0));
                embed.Title = $"Unable to find a guild/realm association!\nTry /wow rankings Realm Name, Guild Name";
                sb.AppendLine($"Command syntax: /wow rankings realm name, guild name");
                sb.AppendLine($"Command example: /wow rankings azgalor, carebears");
                embed.Description = sb.ToString();
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                return;
            }
            try
            {
                var guildMembers = await _wowApi.GetGuildMembersAsync(realmName, guildName, regionName);
                int memberCount = 0;
                if (guildMembers != null)
                {
                    guildName = guildMembers.guild.name;
                    realmName = guildMembers.guild.realm.slug;
                    memberCount = guildMembers.members.Count();
                }
                var wowProgressApi = new WowProgress();
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 255, 0));
                var ranking = wowProgressApi.GetGuildRank(guildName, realmName, regionName);
                var realmObject = wowProgressApi.GetRealmObject(realmName, wowProgressApi._links, regionName);
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

                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex.Message} {ex.InnerException} {ex.Data}{ex.Source}{ex.StackTrace}");
                StringBuilder sb = new StringBuilder();
                var embed = new EmbedBuilder();
                embed.WithColor(new Color(255, 0, 0));
                embed.Title = $":frowning: Sorry, {Context.User.Username}, something went wrong! Perhaps check the guild's home realm.:frowning: ";
                sb.AppendLine($"Command syntax: /wow rankings realm name, guild name");
                sb.AppendLine($"Command example: /wow rankings azgalor, carebears");
                embed.Description = sb.ToString();
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
            }
        }

        [SlashCommand("yoink", "grab users from one voice channel and yoink them into another")]
        [RequireUserPermission(GuildPermission.Administrator)]
        public async Task Yoink(SocketVoiceChannel to, SocketVoiceChannel from)
        {
            // Defer the interaction immediately to avoid timeout with multiple users
            await DeferAsync(ephemeral: true);

            if (from.Id == to.Id)
            {
                await FollowupAsync("Please pick two different voice channels.", ephemeral: true);
                return;
            }

            var usersToMove = from.Users.Where(u => u.VoiceChannel?.Id == from.Id).ToList();

            if (usersToMove.Count == 0)
            {
                await FollowupAsync($"No users currently in [{from.Name}] to move.", ephemeral: true);
                return;
            }

            var movedUsers = 0;
            var skippedUsers = new List<string>();

            foreach (var user in usersToMove)
            {
                try
                {
                    await user.ModifyAsync(u =>
                    {
                        u.Channel = to;
                    });
                    movedUsers++;
                }
                catch (HttpException ex) when (ex.DiscordCode.GetValueOrDefault() == (DiscordErrorCode)40032)
                {
                    // User left voice between gathering the list and moving them
                    skippedUsers.Add(user.Username);
                }

                await Task.Delay(750);
            }

            var message = $"Yoinked [{movedUsers}] users from [{from.Name}] to [{to.Name}]!";

            if (skippedUsers.Count > 0)
            {
                message += $" Skipped {skippedUsers.Count} user(s) no longer in voice: {string.Join(", ", skippedUsers)}.";
            }

            await FollowupAsync(message, ephemeral: true);
        }

        [SlashCommand("member", "give user the member role")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task AddMemberRole(IGuildUser user)
        {
            var serverRoles  = Context.Guild.Roles;
            var userRoles    = user.RoleIds;

            var memberRole   = serverRoles.Where(r => r.Name.ToLower() == "member").FirstOrDefault();
            var raiderRole   = serverRoles.Where(r => r.Name.ToLower() == "raider").FirstOrDefault();

            if (memberRole == null)
            {
                await RespondAsync($"Could not find the [**Member**] role, please add it if you'd like to use this command!");
                return;
            }

            var memberRoleId = memberRole.Id;
            var isMember     = userRoles.Where(u => u == memberRoleId).FirstOrDefault();
            var embed        = new EmbedBuilder();
            var sb           = new StringBuilder();

            embed.WithFooter(new EmbedFooterBuilder
                {
                    Text    = "Message sent from your local, organically grown, NinjaBot!",
                    IconUrl = Context.Guild.IconUrl
                });   

            embed.Title        = $"User role change for [{user.Username}]";
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();

            if (isMember != 0)
            {
                if (raiderRole != null && userRoles.Where(r => r == raiderRole.Id).FirstOrDefault() != 0)
                {
                    await user.RemoveRoleAsync(raiderRole);
                }
                await user.RemoveRoleAsync(memberRole);        
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"Member role removed </3");                
                embed.WithColor(255, 0, 0);                      
            }
            else
            {
                await user.AddRoleAsync(memberRole);                
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"You should now be able to see more channels, welcome to [**{Context.Guild.Name}**]");                
                embed.WithColor(0, 255, 0);                         
            }  
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);                           
        }

        [SlashCommand("raider", "give user the raider role")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task AddRaiderRole(IGuildUser user)
        {
            var serverRoles  = Context.Guild.Roles;
            var userRoles    = user.RoleIds;
            var guild        = (IGuild)Context.Guild;
            var channels     = await guild.GetTextChannelsAsync();
            var raidCat      = guild.GetCategoriesAsync().Result.Where(c => c.Name.ToLower() == "raiding").FirstOrDefault();            
            var raiderRole   = serverRoles.Where(r => r.Name.ToLower() == "raider").FirstOrDefault();
                        
            if (raiderRole == null)
            {
                await RespondAsync($"Could not find the [**Raider**] role, please add it if you'd like to use this command!");
                return;
            }

            ITextChannel signUpChannel = null;
            ITextChannel stratChannel  = null;
            ITextChannel addonChannel  = null;
            ITextChannel logsChannel   = null;

            if (raidCat != null)
            {                
                signUpChannel = channels.Where(c => c.Name.ToLower() == "sign-up" && c.CategoryId == raidCat.Id).FirstOrDefault();
                stratChannel  = channels.Where(c => c.Name.ToLower() == "strategy" && c.CategoryId == raidCat.Id).FirstOrDefault();
                addonChannel  = channels.Where(c => c.Name.ToLower() == "addons" && c.CategoryId == raidCat.Id).FirstOrDefault();
                logsChannel   = channels.Where(c => c.Name.ToLower() == "logs" && c.CategoryId == raidCat.Id).FirstOrDefault();
            }

            var raiderRoleId = raiderRole.Id;
            var isRaider     = userRoles.Where(u => u == raiderRoleId).FirstOrDefault();
            var embed        = new EmbedBuilder();
            var sb           = new StringBuilder();
            
            embed.WithFooter(new EmbedFooterBuilder
                {
                    Text    = "Message sent from your local, organically grown, NinjaBot!",
                    IconUrl = Context.Guild.IconUrl
                });   

            embed.Title        = $"User role change for [{user.Username}]";
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();

            if (isRaider != 0)
            {
                await user.RemoveRoleAsync(raiderRole);        
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"Raider role removed </3");                
                embed.WithColor(255, 0, 0);
                      
            }
            else
            {
                await user.AddRoleAsync(raiderRole);                
                sb.AppendLine($"{user.Mention},");
                sb.AppendLine();
                sb.AppendLine($"You should now be able to see raiding channels, welcome to the [**back2back mirror fam**]");   
                sb.AppendLine();
                sb.AppendLine("<:b2bm:710554622452039731>"); 
                sb.AppendLine();
                if (signUpChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Raid sign-ups are announced in [{signUpChannel.Mention}]");
                }  
                if (addonChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Mandatory addons for raiding are located in [{addonChannel.Mention}]");
                }
                if (stratChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Strats are posted in [{stratChannel.Mention}]");                
                }         
                if (logsChannel != null)
                {
                    sb.AppendLine($":small_blue_diamond: Logs and WoWAnalyzer/Wipefest links are located in [{logsChannel.Mention}]");                
                }                           
                embed.WithColor(0, 255, 0);                         
            }  
            embed.Description = sb.ToString();
            await RespondAsync(embed: embed.Build(), ephemeral: true);                           
        }        
        
        [SlashCommand("listmythic", "list mythic raiders")]
        public async Task ListMythicRaiders()
        {
            var serverRoles      = Context.Guild.Roles;
            var mythicRole       = serverRoles.Where(r => r.Name.ToLower() == "mythic raider").FirstOrDefault();
            var mythicBackupRole = serverRoles.Where(r => r.Name.ToLower() == "mythic backup").FirstOrDefault();
            var guild            = (IGuild)Context.Guild;
            var guildMembers     = await guild.GetUsersAsync();
            var mythicRaiders    = guildMembers.Where(m => m.RoleIds.Contains(mythicRole.Id)).ToList();  
            var mythicBackups    = guildMembers.Where(m => m.RoleIds.Contains(mythicBackupRole.Id)).ToList();
            var sb               = new StringBuilder();

            foreach (var raider in mythicRaiders)
            {                
                if (!string.IsNullOrEmpty(raider.Nickname))
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**{raider.Nickname}**]");
                }
                else
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**none set**]");
                }                
            }

            sb.AppendLine("");
            sb.AppendLine($"Total [{mythicRaiders.Count}]");
            sb.AppendLine("");
            
            sb.AppendLine("__Backups__");
            foreach (var raider in mythicBackups)
            {                
                if (!string.IsNullOrEmpty(raider.Nickname))
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**{raider.Nickname}**]");
                }
                else
                {
                    sb.AppendLine($"<:b2bm:710554622452039731> Username [**{raider.Username}**] Nickname [**none set**]");
                }                
            }

            sb.AppendLine("");
            sb.AppendLine($"Total [{mythicBackups.Count}]");

            var embed = new EmbedBuilder();
            embed.Color = new Color(0, 255, 0);
            embed.Title = $"Mythic Raiders in [{Context.Guild.Name}]";
            embed.ThumbnailUrl = Context.Guild.IconUrl;
            embed.Description = sb.ToString();
            embed.WithFooter(new EmbedFooterBuilder
                {
                    Text    = "Message sent from your local, organically grown, NinjaBot!",
                    IconUrl = Context.Guild.IconUrl
                });

            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }
    }
}
