using Discord;
using Discord.Net;
using Discord.WebSocket;
using Discord.Interactions;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NinjaBotCore.Database;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace NinjaBotCore.Modules.Interactions.Admin
{
    public class Admin : NinjaBotBaseModule
    {
        private static bool _isLinked = false;
        private static DiscordShardedClient _client;
        private readonly IConfigurationRoot _config;
        private readonly ILogger<Admin> _logger;

        public Admin(IServiceProvider services)
            : base(services.GetRequiredService<IServiceScopeFactory>())
        {
            _client = services.GetRequiredService<DiscordShardedClient>();
            _logger = services.GetRequiredService<ILogger<Admin>>();
            if (!_isLinked)
            {
                _client.MessageReceived += WordFinder;
                _logger.LogInformation("Hooked into MessageReceived for Admin word filter.");
            }
            _isLinked = true;
            _config = services.GetRequiredService<IConfigurationRoot>();
            _logger.LogInformation("Admin module loaded!");
        }

        private async Task WordFinder(SocketMessage messageDetails)
        {
            await Task.Run(async () =>
            {
                var message = messageDetails as SocketUserMessage;
                if (!messageDetails.Author.IsBot)
                {
                    SocketGuild guild = (message.Channel as SocketGuildChannel)?.Guild;
                    var serverWordList = await WithDbAsync(async db =>
                    {
                        return await db.WordList.Where(w => w.ServerId == (long)guild.Id).ToListAsync();
                    });

                    bool wordFound = false;
                    foreach (var singleWord in serverWordList)
                    {
                        foreach (var content in messageDetails.Content.ToLower().Split(' '))
                        {
                            if (singleWord.Word.ToLower().Contains(content))
                            {
                                wordFound = true;
                            }
                        }
                    }
                    if (wordFound)
                    {
                        await messageDetails.DeleteAsync();
                    }
                }
            });
        }     

        [SlashCommand("leave-server", "leave a server")]        
        [RequireOwner]
        public async Task LeaveServer(ulong serverId)
        {
            await _client.GetGuild(serverId).LeaveAsync();
        }

        [SlashCommand("add-wow-resource", "add a wow resource")]
        [RequireOwner]
        public async Task AddWoWResource(string args = null)
        {
            if (args != null)
            {
                try
                {
                    int argCount = args.Split(',').Count();
                    if (argCount == 4)
                    {
                        await WithDbAsync(async db =>
                        {
                            db.WowResources.Add(new WowResources
                            {
                                ClassName = args.Split(',')[0].Trim(),
                                Specialization = args.Split(',')[1].Trim(),
                                Resource = args.Split(',')[2].Trim(),
                                ResourceDescription = args.Split(',')[3].Trim(),
                            });
                            await db.SaveChangesAsync();
                        });
                    }
                }
                catch (Exception ex)
                {
                    await RespondAsync($"Error adding resource: [{ex.Message}]", ephemeral: true);
                }
            }
        }

        [SlashCommand("remove-wow-resource", "remove wow resource")]
        [RequireOwner]
        public async Task RemoveWoWResource(int resourceId = 0)
        {
            if (resourceId > 0)
            {
                try
                {
                    await WithDbAsync(async db =>
                    {
                        var resource = await db.WowResources.FirstOrDefaultAsync(r => r.Id == resourceId);
                        if (resource != null)
                        {
                            db.WowResources.Remove(resource);
                            await db.SaveChangesAsync();
                        }
                    });
                }
                catch (Exception ex)
                {
                    await RespondAsync($"Error removing resource: [{ex.Message}]", ephemeral: true);
                }
            }
        }

        [SlashCommand("list-wow-resources", "list wow resources")]
        public async Task ListWoWResource(string args = null)
        {
            var resources = await WithDbAsync(async db =>
            {
                return await db.WowResources.Where(r => r.ClassName.ToLower().Contains(args)).ToListAsync();
            });

            if (resources != null)
            {
                var embed = new EmbedBuilder();
                embed.Title = $"WoW Resource List Search: [{args}]";
                foreach (var resource in resources)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine($"Class: [{resource.ClassName}]");
                    sb.AppendLine($"Specialization: [{resource.Specialization}]");
                    sb.AppendLine($"Resource: [{resource.Resource}]");
                    sb.AppendLine($"ResourceDescription: [{resource.ResourceDescription}]");
                    embed.AddField(new EmbedFieldBuilder
                    {
                        Name = $"{resource.Id}",
                        Value = sb.ToString()
                    });
                }
                await RespondAsync(embed: embed.Build(), ephemeral: true);
            }
        }

        [SlashCommand("numservers", "list number of servers the bot is in")]
        [RequireOwner]
        public async Task GetNumGuilds()
        {
            var client = (IDiscordClient)Context.Client;            
            var numGuilds = await client.GetGuildsAsync();
            await RespondAsync($"I am connected to {numGuilds.Count()} guilds!", ephemeral: true);
        }

        [SlashCommand("add-word", "add word to blacklist")]
        [RequireOwner]
        public async Task AddWord(string word)
        {
            var sb = new StringBuilder();
            await WithDbAsync(async db =>
            {
                var words = await db.WordList.Where(w => w.ServerId == (long)Context.Guild.Id).ToListAsync();
                bool wordFound = false;
                foreach (var singleWord in words)
                {
                    if (singleWord.Word.ToLower().Contains(word.ToLower()))
                    {
                        wordFound = true;
                    }
                }
                if (wordFound)
                {
                    sb.AppendLine($"[{word}] is already in the list!");
                }
                else
                {
                    sb.AppendLine($"Adding [{word}] to the list!");
                    db.Add(new WordList
                    {
                        ServerId = (long)Context.Guild.Id,
                        ServerName = Context.Guild.Name,
                        Word = word,
                        SetById = (long)Context.User.Id
                    });
                    await db.SaveChangesAsync();
                }
            });
            await RespondAsync(sb.ToString(), ephemeral: true);
        }        
    
        [SlashCommand("force-greeting-clear", "force a greeting clear")]
        [RequireOwner]
        public async Task ForceGreetingClear(long serverId)
        {
            var greetingInfo = await WithDbAsync(async db =>
            {
                return await db.ServerGreetings.FirstOrDefaultAsync(g => g.DiscordGuildId == serverId);
            });

            if (greetingInfo != null)
            {
                try
                {
                    await WithDbAsync(async db =>
                    {
                        var greeting = await db.ServerGreetings.FirstOrDefaultAsync(g => g.DiscordGuildId == serverId);
                        if (greeting != null)
                        {
                            db.Remove(greeting);
                            await db.SaveChangesAsync();
                        }
                    });
                    await RespondAsync("Cleared!");
                }
                catch (Exception ex)
                {
                    await RespondAsync($"Error clearing greeting -> [{ex.Message}]", ephemeral: true);
                }
            }
            else
            {
                await RespondAsync($"No association found for [{serverId}]!", ephemeral: true);
            }
        }
    }
}
