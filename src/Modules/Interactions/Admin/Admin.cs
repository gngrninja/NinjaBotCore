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
using NinjaBotCore.Modules.Wow;

namespace NinjaBotCore.Modules.Interactions.Admin
{
    public class Admin : NinjaBotBaseModule
    {
        private readonly DiscordShardedClient _client;
        private readonly IConfigurationRoot _config;
        private readonly ILogger<Admin> _logger;
        private readonly WordFilterService _wordFilterService;
        private readonly WarcraftLogs _warcraftLogs;

        // Event handling is now done by WordFilterService
        // This module only handles slash commands
        public Admin(IServiceProvider services)
            : base(services.GetRequiredService<IServiceScopeFactory>())
        {
            _client = services.GetRequiredService<DiscordShardedClient>();
            _logger = services.GetRequiredService<ILogger<Admin>>();
            _config = services.GetRequiredService<IConfigurationRoot>();
            _wordFilterService = services.GetRequiredService<WordFilterService>();
            _warcraftLogs = services.GetRequiredService<WarcraftLogs>();
            _logger.LogInformation("Admin module loaded!");
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
                    var parts = args.Split(',');
                    if (parts.Length == 4)
                    {
                        await WithDbAsync(async db =>
                        {
                            db.WowResources.Add(new WowResources
                            {
                                ClassName = parts[0].Trim(),
                                Specialization = parts[1].Trim(),
                                Resource = parts[2].Trim(),
                                ResourceDescription = parts[3].Trim(),
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
            if (string.IsNullOrWhiteSpace(args))
            {
                await RespondAsync("Please provide a search term (e.g., class name).", ephemeral: true);
                return;
            }

            var resources = await WithDbAsync(async db =>
            {
                return await db.WowResources.Where(r => EF.Functions.ILike(r.ClassName, $"%{args}%")).ToListAsync();
            });

            if (resources != null && resources.Any())
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
            else
            {
                await RespondAsync($"No WoW resources found matching '{args}'.", ephemeral: true);
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
                _wordFilterService.InvalidateWordListCache(serverId);
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
                _wordFilterService.InvalidateWordListCache(serverId);
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

        [SlashCommand("refresh-raid-tier", "Refresh current raid tier from WarcraftLogs API")]
        [RequireOwner]
        public async Task RefreshRaidTier(
            [Summary("expansion", "Expansion ID (0=auto, 10=The War Within, 9=Dragonflight)")]
            int expansionId = 0)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                var currentTier = WarcraftLogs.CurrentRaidTier;
                var oldTierName = currentTier?.RaidName ?? "none";
                var oldZoneId = currentTier?.WclZoneId ?? 0;

                var newTier = await _warcraftLogs.RefreshCurrentRaidTierAsync(expansionId);

                if (newTier == null)
                {
                    await FollowupAsync("Failed to detect current raid tier from API.", ephemeral: true);
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle("Raid Tier Refreshed")
                    .WithColor(new Color(0, 200, 100))
                    .AddField("Previous", $"{oldTierName} (Zone ID: {oldZoneId})", true)
                    .AddField("Current", $"{newTier.RaidName} (Zone ID: {newTier.WclZoneId})", true)
                    .AddField("Partition", newTier.Partition?.ToString() ?? "default", true)
                    .WithFooter($"Expansion ID: {expansionId}")
                    .Build();

                await FollowupAsync(embed: embed, ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing raid tier");
                await FollowupAsync($"Error: {ex.Message}", ephemeral: true);
            }
        }
    }
}
