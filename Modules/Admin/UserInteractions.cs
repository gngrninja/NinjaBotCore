using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Microsoft.Extensions.DependencyInjection;
using Discord.Net;
using Discord.Commands;
using Discord.WebSocket;
using NinjaBotCore.Database;

namespace NinjaBotCore.Modules.Admin
{
    public class UserInteraction
    {
        private DiscordSocketClient _client;
        private readonly IServiceProvider _provider;
        private ChannelCheck _cc;

        public UserInteraction(IServiceProvider provider)
        {
            _provider = provider;
            _client = _provider.GetService<DiscordSocketClient>();
            _cc = _provider.GetService<ChannelCheck>();
            _client.UserJoined += HandleGreeting;
            _client.UserLeft += HandleParting;
            Console.WriteLine($"UserInteractions loaded");
        }

        private async Task HandleGreeting(SocketGuildUser user)
        {
            //Maybe new ver of reply in ChannelCheck class? 
            ServerGreeting shouldGreet = GetGreeting(user);
            if (shouldGreet != null && shouldGreet.GreetUsers == true)
            {
                StringBuilder sb = new StringBuilder();
                ISocketMessageChannel messageChannel = null;
                if (shouldGreet.GreetingChannelId != 0)
                {
                    messageChannel = user.Guild.GetChannel((ulong)shouldGreet.GreetingChannelId) as ISocketMessageChannel;
                }
                else 
                {
                    messageChannel = user.Guild.DefaultChannel as ISocketMessageChannel;
                }     
                var embed = new EmbedBuilder();
                embed.Title = $"[{user.Username}] has joined [**{user.Guild.Name}**]!";
                if (string.IsNullOrEmpty(shouldGreet.Greeting))
                {
                    sb.AppendLine($"Welcome them! :hugging:");
                    sb.AppendLine($"(or not, :shrug:)");
                    embed.Description = sb.ToString();
                }
                else
                {
                    embed.Description = shouldGreet.Greeting;
                }
                embed.ThumbnailUrl = user.GetAvatarUrl();
                embed.WithColor(new Color(0, 255, 0));
                await messageChannel.SendMessageAsync("", false, embed);
            }
        }

        private async Task HandleParting(SocketGuildUser user)
        {
            ServerGreeting shouldGreet = GetGreeting(user);
            if (shouldGreet != null && shouldGreet.GreetUsers == true)
            {
                ISocketMessageChannel messageChannel = null;
                if (shouldGreet.GreetingChannelId != 0)
                {
                    messageChannel = user.Guild.GetChannel((ulong)shouldGreet.GreetingChannelId) as ISocketMessageChannel;
                }
                else 
                {
                    messageChannel = user.Guild.DefaultChannel as ISocketMessageChannel;
                }                
                if (messageChannel != null)
                {
                    var embed = new EmbedBuilder();
                    embed.Title = $"[{user.Username}] has left [**{user.Guild.Name}**]!";
                    if (string.IsNullOrEmpty(shouldGreet.PartingMessage))
                    {
                        embed.Description = $"Fine, be that way! :wave:";
                    }
                    else
                    {
                        embed.Description = shouldGreet.PartingMessage;
                    }
                    embed.ThumbnailUrl = user.GetAvatarUrl();
                    embed.WithColor(new Color(255, 0, 0));
                    await messageChannel.SendMessageAsync("", false, embed);
                }
            }
        }

        private ServerGreeting GetGreeting(SocketGuildUser user)
        {
            ServerGreeting shouldGreet = null;
            var guildId = user.Guild.Id;
            using (var db = new NinjaBotEntities())
            {
                shouldGreet = db.ServerGreetings.Where(g => g.DiscordGuildId == (long)guildId).FirstOrDefault();
            }
            return shouldGreet;
        }
    }
}