using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Microsoft.Extensions.DependencyInjection;
using Discord.WebSocket;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;
using NinjaBotCore.Services;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Modules.Admin
{
    public class UserInteraction : IDisposable
    {
        private readonly ILogger _logger;
        private readonly DiscordShardedClient _client;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly WowCacheService _greetingCache;
        private bool _disposed;

        // NOTE: UserInteraction is registered as Singleton to hook Discord events at startup
        // Therefore, it cannot use scoped repository injection (Pattern #3)
        // Instead, it uses Pattern #1 (GetRepository) like AwayCommands and WowUtilities
        public UserInteraction(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<UserInteraction>>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
            _greetingCache = services.GetRequiredService<WowCacheService>();

            services.GetRequiredService<DiscordShardedClient>().UserJoined += HandleGreeting;
            services.GetRequiredService<DiscordShardedClient>().UserLeft += HandleParting;
            services.GetRequiredService<DiscordShardedClient>().ModalSubmitted += HandleModal;

            _logger.LogInformation($"UserInteractions loaded");
        }

        // Pattern #1: Create repository on-demand (singleton service)
        private IRepository<TEntity> GetRepository<TEntity>() where TEntity : class
        {
            return new Repository<TEntity>(_scopeFactory);
        }

        private async Task HandleModal(SocketModal modal)
        {
            // Defer the interaction immediately to prevent timeout (Discord requires response within 3 seconds)
            await modal.DeferAsync(ephemeral: true);

            // Get the values of components.
            List<SocketMessageComponentData> components =
                modal.Data.Components.ToList();
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            var guildInfo = _client.GetGuild((ulong)modal.GuildId);
            switch (modal.Data.CustomId)
            {
                case "joining_message":
                {
                    await HandleJoiningModal(modal, components, embed, sb, guildInfo);
                    break;
                }
                case "parting_message":
                {
                    await HandlePartingModal(modal, components, embed, sb, guildInfo);
                    break;
                }
                case "discord_server_note":
                {
                    await HandleNoteModal(modal, components, embed, sb, guildInfo);
                    break;
                }
            }
        }

        private async Task HandlePartingModal(SocketModal modal, List<SocketMessageComponentData> components, EmbedBuilder embed, StringBuilder sb, SocketGuild guildInfo)
        {
            string partingMessage = components.First(x => x.CustomId == "parting_message").Value;
            if (!string.IsNullOrEmpty(partingMessage))
            {
                try
                {
                    embed.Title = $"Parting message change for {guildInfo.Name}";
                    sb.AppendLine("New message:");
                    sb.AppendLine(partingMessage);

                    await using var greetingRepo = GetRepository<ServerGreeting>();
                    await greetingRepo.UpsertAsync(
                        findPredicate: g => g.DiscordGuildId == (long)modal.GuildId,
                        updateAction: greeting =>
                        {
                            greeting.PartingMessage = partingMessage.Trim();
                            greeting.SetById = (long)modal.User.Id;
                            greeting.SetByName = modal.User.Username;
                            greeting.TimeSet = DateTime.UtcNow;
                        },
                        createFactory: () => new ServerGreeting
                        {
                            DiscordGuildId = (long)modal.GuildId,
                            PartingMessage = partingMessage.Trim(),
                            SetById = (long)modal.User.Id,
                            SetByName = modal.User.Username,
                            TimeSet = DateTime.UtcNow
                        });
                    await greetingRepo.SaveChangesAsync();
                    _greetingCache.InvalidateServerGreeting((long)modal.GuildId);
                }
                catch (Exception)
                {
                    embed.Title = $"Error changing message";
                    sb.AppendLine($"{modal.User.Mention},");
                    sb.AppendLine($"I've encountered an error, please contact the owner for help.");
                }
            }
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = guildInfo.IconUrl;

            await modal.FollowupAsync(text: null, embed: embed.Build(), ephemeral: true);
        }

        private async Task HandleJoiningModal(SocketModal modal, List<SocketMessageComponentData> components, EmbedBuilder embed, StringBuilder sb, SocketGuild guildInfo)
        {
            string joiningMessage = components.First(x => x.CustomId == "joining_message").Value;
            if (!string.IsNullOrEmpty(joiningMessage))
            {
                try
                {
                    embed.Title = $"Joining message change for {guildInfo.Name}";
                    sb.AppendLine("New message:");
                    sb.AppendLine(joiningMessage);

                    await using var greetingRepo = GetRepository<ServerGreeting>();
                    await greetingRepo.UpsertAsync(
                        findPredicate: g => g.DiscordGuildId == (long)modal.GuildId,
                        updateAction: greeting =>
                        {
                            greeting.Greeting = joiningMessage.Trim();
                            greeting.SetById = (long)modal.User.Id;
                            greeting.SetByName = modal.User.Username;
                            greeting.TimeSet = DateTime.UtcNow;
                        },
                        createFactory: () => new ServerGreeting
                        {
                            DiscordGuildId = (long)modal.GuildId,
                            Greeting = joiningMessage.Trim(),
                            SetById = (long)modal.User.Id,
                            SetByName = modal.User.Username,
                            TimeSet = DateTime.UtcNow
                        });
                    await greetingRepo.SaveChangesAsync();
                    _greetingCache.InvalidateServerGreeting((long)modal.GuildId);
                }
                catch (Exception)
                {
                    embed.Title = $"Error changing message";
                    sb.AppendLine($"{modal.User.Mention},");
                    sb.AppendLine($"I've encountered an error, please contact the owner for help.");
                }
            }
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = guildInfo.IconUrl;

            await modal.FollowupAsync(text: null, embed: embed.Build(), ephemeral: true);
        }

        private async Task HandleNoteModal(SocketModal modal, List<SocketMessageComponentData> components, EmbedBuilder embed, StringBuilder sb, SocketGuild guildInfo)
        {
            string noteText = components.First(x => x.CustomId == "note_text").Value;
            try
            {
                await using var noteRepo = GetRepository<Note>();
                await noteRepo.UpsertAsync(
                    findPredicate: n => n.ServerId == (long)guildInfo.Id,
                    updateAction: note =>
                    {
                        note.Note1 = noteText;
                        note.SetBy = modal.User.Username;
                        note.SetById = (long)modal.User.Id;
                        note.TimeSet = DateTime.UtcNow;
                    },
                    createFactory: () => new Note
                    {
                        Note1 = noteText,
                        ServerId = (long)guildInfo.Id,
                        ServerName = guildInfo.Name,
                        SetBy = modal.User.Username,
                        SetById = (long)modal.User.Id,
                        TimeSet = DateTime.UtcNow
                    });
                await noteRepo.SaveChangesAsync();
                sb.AppendLine($"Note successfully added for server [**{guildInfo.Name}**] by [**{modal.User.Username}**]!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting note {ex.Message}");
                sb.AppendLine($"Something went wrong adding a note for server [**{guildInfo.Name}**] :(");
            }
            embed.Title = $":notepad_spiral:Notes for {guildInfo.Name}:notepad_spiral:";
            embed.Description = sb.ToString();
            embed.ThumbnailUrl = guildInfo.IconUrl;
            embed.WithColor(new Color(0, 255, 0));

            await modal.FollowupAsync(embed: embed.Build(), ephemeral: true);
        }

        private async Task HandleParting(SocketGuild guild, SocketUser user)
        {
            ServerGreeting shouldGreet = await _greetingCache.GetServerGreetingAsync((long)guild.Id);
                if (shouldGreet != null && shouldGreet.GreetUsers == true)
                {
                    var sb = new StringBuilder();
                    ISocketMessageChannel messageChannel = null;
                    try
                    {
                        if (shouldGreet.GreetingChannelId != 0)
                        {
                            if (shouldGreet.PartingChannelId != null)
                            {
                                messageChannel = guild.GetChannel((ulong)shouldGreet.PartingChannelId) as ISocketMessageChannel;
                            }
                            else
                            {
                                messageChannel = guild.GetChannel((ulong)shouldGreet.GreetingChannelId) as ISocketMessageChannel;
                            }
                        }
                        else
                        {
                            messageChannel = guild.DefaultChannel as ISocketMessageChannel;
                        }
                        if (messageChannel != null)
                        {
                            var embed = new EmbedBuilder();
                            embed.Title = $"[{user.Username}] has left [**{guild.Name}**]!";
                            sb.AppendLine($"{user.Username}");
                            if (string.IsNullOrEmpty(shouldGreet.PartingMessage))
                            {
                                sb.AppendLine($"Fine, be that way! :wave:");
                            }
                            else
                            {
                                sb.AppendLine($"{shouldGreet.PartingMessage}");
                            }
                            embed.Description = sb.ToString();
                            embed.ThumbnailUrl = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
                            embed.WithColor(new Color(255, 0, 0));
                            await messageChannel.SendMessageAsync("", false, embed.Build());
                        }
                    }
                    catch (Exception ex)
                    {
                        if (messageChannel != null)
                        {
                            _logger.LogError($"Error with channel -> [{messageChannel.Name}] on [{guild.Name}] -> [{guild.Id}] -> [{ex.Message}]");
                        }
                        else
                        {
                            _logger.LogError($"Error with no channel -> [{guild.Name}] -> [{guild.Id}] -> [{ex.Message}]");
                        }
                    }
                }
        }

        private async Task HandleGreeting(SocketGuildUser user)
        {
            ServerGreeting shouldGreet = await GetGreetingAsync(user);
                if (shouldGreet != null && shouldGreet.GreetUsers == true)
                {
                    var sb = new StringBuilder();   
                    ISocketMessageChannel messageChannel = null;
                    try
                    {                                             
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
                        embed.ThumbnailUrl = user.GetAvatarUrl() ?? user.GetDefaultAvatarUrl();
                        embed.WithColor(new Color(0, 255, 0));
                        await messageChannel.SendMessageAsync("", false, embed.Build());
                    }
                    catch (Exception ex)
                    {
                        if (messageChannel != null)
                        {
                            _logger.LogError($"Error with channel -> [{messageChannel.Name}] on [{user.Guild.Name}] -> [{user.Guild.Id}] -> [{ex.Message}]");
                        }
                        else
                        {
                            _logger.LogError($"Error with no channel -> [{user.Guild.Name}] -> [{user.Guild.Id}] -> [{ex.Message}]");
                        }
                    }
                }
        }

        private async Task<ServerGreeting> GetGreetingAsync(SocketGuildUser user)
        {
            var guildId = user.Guild.Id;
            return await _greetingCache.GetServerGreetingAsync((long)guildId);
        }

        /// <summary>
        /// Disposes resources and unsubscribes from event handlers
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _client.UserJoined -= HandleGreeting;
                _client.UserLeft -= HandleParting;
                _client.ModalSubmitted -= HandleModal;

                _logger.LogInformation("UserInteraction disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing UserInteraction");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
