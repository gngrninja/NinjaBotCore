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
using NinjaBotCore.Migrations;
using Microsoft.Extensions.Caching.Memory;

namespace NinjaBotCore.Modules.Interactions.Admin
{
    public class Admin : NinjaBotBaseModule
    {
        private static bool _isLinked = false;
        private static DiscordShardedClient _client;
        private readonly IConfigurationRoot _config;
        private readonly ILogger<Admin> _logger;
        private static readonly MemoryCache _wordListCache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 1000
        });

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
            try
            {
                // Early returns - fast filtering
                if (messageDetails.Author.IsBot) return;
                if (!(messageDetails.Channel is SocketGuildChannel guildChannel)) return; // Skip DMs
                if (string.IsNullOrWhiteSpace(messageDetails.Content)) return;

                var serverId = (long)guildChannel.Guild.Id;
                var cacheKey = $"wordlist_{serverId}";

                // Get from cache or DB (15 minute cache)
                var bannedWords = await _wordListCache.GetOrCreateAsync(cacheKey, async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
                    entry.Size = 1;

                    return await WithDbAsync(async db =>
                    {
                        return await db.WordList
                            .Where(w => w.ServerId == serverId)
                            .Select(w => w.Word.ToLower())
                            .ToListAsync();
                    });
                });

                // Fast check - does message contain any banned word?
                var messageContent = messageDetails.Content.ToLower();
                if (bannedWords.Any(word => messageContent.Contains(word)))
                {
                    await messageDetails.DeleteAsync();
                    _logger.LogInformation("Deleted message from {User} in {Guild} - contained banned word",
                        messageDetails.Author.Username, guildChannel.Guild.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in word filter for channel {ChannelId}",
                    messageDetails.Channel.Id);
            }
        }

        /// <summary>
        /// Invalidate the word list cache for a server when words are added/removed
        /// </summary>
        private void InvalidateWordListCache(long serverId)
        {
            var cacheKey = $"wordlist_{serverId}";
            _wordListCache.Remove(cacheKey);
            _logger.LogInformation("Invalidated word list cache for server {ServerId}", serverId);
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
            var serverId = (long)Context.Guild.Id;

            var wasAdded = await WithDbAsync(async db =>
            {
                var foundWord = await db.WordList
                    .FirstOrDefaultAsync(w => w.ServerId == serverId && w.Word.ToLower() == word.ToLower());

                if (foundWord != null)
                {
                    sb.AppendLine($"[{word}] is already in the list!");
                    return false;
                }
                else
                {
                    sb.AppendLine($"Adding [{word}] to the list!");
                    db.Add(new WordList
                    {
                        ServerId = serverId,
                        ServerName = Context.Guild.Name,
                        Word = word,
                        SetById = (long)Context.User.Id
                    });
                    await db.SaveChangesAsync();
                    return true;
                }
            });

            // Invalidate cache if word was added
            if (wasAdded)
            {
                InvalidateWordListCache(serverId);
            }

            await RespondAsync(sb.ToString(), ephemeral: true);
        }        
    
        [SlashCommand("remove-word", "remove a word from the blacklist")]
        [RequireOwner]
        public async Task RemoveWord(string word)
        {
            var sb = new StringBuilder();
            var serverId = (long)Context.Guild.Id;

            var deletedCount = await WithDbAsync(async db =>
            {
                try
                {
                    var searchWord = word.ToLower();
                    var count = await db.WordList
                        .Where(w => w.ServerId == serverId && w.Word.ToLower() == searchWord)
                        .ExecuteDeleteAsync();

                    if (count > 0)
                    {
                        sb.AppendLine($"[{word}] removed!");
                    }
                    else
                    {
                        sb.AppendLine($"[{word}] not found in the database!");
                    }

                    return count;
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"Error attempting to remove: [{word}] -> [{ex.Message}]");
                    return 0;
                }
            });

            // Invalidate cache if word was removed
            if (deletedCount > 0)
            {
                InvalidateWordListCache(serverId);
            }

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
