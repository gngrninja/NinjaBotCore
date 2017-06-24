using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord.Net;
using Discord.WebSocket;
using Discord;
using Discord.Commands;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Giphy;

namespace NinjaBotCore.Modules.Giphy
{
    public class GiphyCommands : ModuleBase
    {
        private static ChannelCheck _cc = null;
        public GiphyCommands(ChannelCheck cc)
        {
            if (_cc == null)
            {
                _cc = cc;
            }
        }

        [Command("giphy", RunMode = RunMode.Async)]
        [Alias("gif")]
        [Summary("Searches for a gif to reply to. You can optionally provide a search term.")]
        public async Task Giphy([Remainder] string args = "")
        {            
            bool isEnabled = CheckGiphyEnabled(Context);
            var embed = new EmbedBuilder();
            embed.WithColor(new Color(0, 255, 255));
            if (isEnabled)
            {
                GiphyApi api = new GiphyApi();
                StringBuilder sb = new StringBuilder();
                GiphyReponse r = new GiphyReponse();                
                try
                {
                    if (string.IsNullOrEmpty(args))
                    {
                        r = api.GetRandomImage(string.Empty);
                        embed.Title = $"__Giphy for [**{Context.User.Username}**]__";
                    }
                    else
                    {                                              
                        r = api.GetRandomImage(args);                        
                        embed.Title = $"__Giphy for [**{Context.User.Username}**] ({args})__";
                    }                                                        
                    
                    embed.ImageUrl = r.data.fixed_height_small_url;
                    await _cc.Reply(Context, embed);
                }
                catch (Exception ex)
                {
                    await _cc.Reply(Context, "Sorry, something went wrong :(");
                    Console.WriteLine($"Giphy Command Error -> [{ex.Message}]");
                }
            }
            else
            {
                embed.Title = $"Sorry, Giphy is disabled here :(\n";
                embed.Description = $"Use {NinjaBot.Prefix}giphy-toggle to enable it";
                await _cc.Reply(Context, embed);
            }
        }

        [Command("giphy-toggle", RunMode = RunMode.Async)]
        [Alias("gif-toggle")]
        [Summary("Toggle Giphy on/off for the server")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task ToggleGiphy()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                string serverName = string.Empty;
                long serverId = 0;
                var guildInfo = Context.Guild;
                var embed = new EmbedBuilder();
                if (guildInfo == null)
                {
                    serverName = Context.User.Username;
                    serverId = (long)Context.User.Id;
                    embed.ThumbnailUrl = Context.User.GetAvatarUrl();
                }
                else
                {
                    serverName = Context.Guild.Name;
                    serverId = (long)Context.Guild.Id;
                    embed.ThumbnailUrl = Context.Guild.IconUrl;
                }                
                using (var db = new NinjaBotEntities())
                {
                    var giphySettings = db.Giphy.FirstOrDefault(g => g.ServerName == serverName);
                    if (giphySettings == null)
                    {
                        db.Giphy.Add(new Database.Giphy
                        {
                            GiphyEnabled = true,
                            ServerId = serverId,
                            ServerName = serverName
                        });
                        embed.Title = $"Giphy enabled for [**{serverName}**]";                        
                        sb.AppendLine($":question:__How to use **!giphy**__:question:");
                        sb.AppendLine();
                        sb.AppendLine($"**{NinjaBot.Prefix}giphy**");
                        sb.AppendLine($"The above command would get a random image");
                        sb.AppendLine();
                        sb.AppendLine($"**{NinjaBot.Prefix}giphy Rocket League**");
                        sb.AppendLine($"The above command would get a random image related to Rocket League");
                    }
                    else if ((bool)giphySettings.GiphyEnabled)
                    {
                        giphySettings.GiphyEnabled = false;
                        embed.Title = $"Giphy disabled for [**{serverName}**]";
                        sb.AppendLine();
                        sb.AppendLine($"{Context.User.Username}, I thought this was America!");                        
                    }
                    else
                    {
                        giphySettings.GiphyEnabled = true;
                        embed.Title = $"Giphy enabled for [**{serverName}**]";                        
                        sb.AppendLine($":question:__How to use **!giphy**__:question:");
                        sb.AppendLine();
                        sb.AppendLine($"**{NinjaBot.Prefix}giphy**");
                        sb.AppendLine($"The above command would get a random image");
                        sb.AppendLine();
                        sb.AppendLine($"**{NinjaBot.Prefix}giphy Rocket League**");
                        sb.AppendLine($"The above command would get a random image related to Rocket League");
                    }
                    db.SaveChanges();
                }                
                embed.WithColor(new Color(255, 0, 0));                
                embed.Description = sb.ToString();
                await _cc.Reply(Context, embed);
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Giphy toggle error -> [{ex.Message}]");
                await _cc.Reply(Context, $"Error toggling Giphy command...!");
            }            
        }

        [Command("check-giphy", RunMode = RunMode.Async)]
        [Summary("Check to see if giphy is enabled here")]
        public async Task CheckGiphy()
        {
            bool enabled = CheckGiphyEnabled(Context);
            await _cc.Reply(Context, enabled.ToString());
        }

        private static bool CheckGiphyEnabled(ICommandContext context)
        {
            bool isEnabled = false;
            string serverName = string.Empty;
            var guildInfo = context.Guild;
            if (guildInfo == null)
            {
                serverName = context.User.Username;
            }
            else
            {
                serverName = context.Guild.Name;
            }
            using (var db = new NinjaBotEntities())
            {
                var giphySettings = db.Giphy.FirstOrDefault(g => g.ServerName == serverName);
                if (giphySettings != null)
                {                    
                    if ((bool)giphySettings.GiphyEnabled)
                    {
                        isEnabled = true;
                    }
                }               
            }
            return isEnabled;
        }
    }
}