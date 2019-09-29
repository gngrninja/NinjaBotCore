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
using NinjaBotCore.Services;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Modules.Admin
{
    public class UserInteraction
    {
        private readonly ILogger _logger;

        public UserInteraction(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<UserInteraction>>();
            services.GetRequiredService<DiscordShardedClient>().UserJoined += HandleGreeting;
            services.GetRequiredService<DiscordShardedClient>().UserLeft += HandleParting;
            _logger.LogInformation($"UserInteractions loaded");
        }

        private async Task HandleGreeting(SocketGuildUser user)
        {
            await Task.Run(async () =>
            {
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
                    sb.AppendLine($"{user.Mention}");
                    if (string.IsNullOrEmpty(shouldGreet.Greeting))
                    {
                        sb.AppendLine($"Welcome them! :hugging:");
                        sb.AppendLine($"(or not, :shrug:)");
                    }
                    else
                    {
                        sb.AppendLine($"{shouldGreet.Greeting}");
                    }
                    embed.Description = sb.ToString();
                    embed.ThumbnailUrl = user.GetAvatarUrl();
                    embed.WithColor(new Color(0, 255, 0));
                    await messageChannel.SendMessageAsync("", false, embed.Build());
                }
            });
        }

        private async Task HandleParting(SocketGuildUser user)
        {
            await Task.Run(async () =>
            {
                ServerGreeting shouldGreet = GetGreeting(user);
                var sb = new StringBuilder();
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
                        sb.AppendLine($"{user.Mention}");
                        if (string.IsNullOrEmpty(shouldGreet.PartingMessage))
                        {
                            sb.AppendLine($"Fine, be that way! :wave:");
                        }
                        else
                        {
                            sb.AppendLine($"{shouldGreet.PartingMessage}");
                        }
                        embed.Description = sb.ToString();
                        embed.ThumbnailUrl = user.GetAvatarUrl();
                        embed.WithColor(new Color(255, 0, 0));
                        await messageChannel.SendMessageAsync("", false, embed.Build());
                    }
                }
            });
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