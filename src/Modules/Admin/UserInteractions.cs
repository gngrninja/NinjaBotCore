using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Microsoft.Extensions.DependencyInjection;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Polls;
using NinjaBotCore.Repositories;
using NinjaBotCore.Services;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Modules.Admin
{
    public class UserInteraction : IDisposable
    {
        private static readonly Regex DurationRegex = new(@"^(\d+)(h|d|w)$", RegexOptions.Compiled);

        private readonly ILogger _logger;
        private readonly DiscordShardedClient _client;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IServiceProvider _services;
        private readonly WowCacheService _greetingCache;
        private bool _disposed;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _handledInteractions = new();

        // NOTE: UserInteraction is registered as Singleton to hook Discord events at startup
        // Therefore, it cannot use scoped repository injection (Pattern #3)
        // Instead, it uses Pattern #1 (GetRepository) like AwayCommands and WowUtilities
        public UserInteraction(IServiceProvider services)
        {
            _services = services;
            _logger = services.GetRequiredService<ILogger<UserInteraction>>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
            _greetingCache = services.GetRequiredService<WowCacheService>();

            services.GetRequiredService<DiscordShardedClient>().UserJoined += HandleGreeting;
            services.GetRequiredService<DiscordShardedClient>().UserLeft += HandleParting;
            // Subscribe to specific events instead of generic InteractionCreated to avoid conflicts with InteractionHandler
            services.GetRequiredService<DiscordShardedClient>().ModalSubmitted += HandleModalSubmitted;
            services.GetRequiredService<DiscordShardedClient>().ButtonExecuted += HandleButtonExecuted;

            _logger.LogInformation($"UserInteractions loaded");
        }

        // Pattern #1: Create repository on-demand (singleton service)
        private IRepository<TEntity> GetRepository<TEntity>() where TEntity : class
        {
            return new Repository<TEntity>(_scopeFactory);
        }

        /// <summary>
        /// Legacy modal event handler - kept for any future non-attribute modals.
        /// All current modals use [ModalInteraction] attribute handlers:
        /// - joining_message, parting_message, discord_server_note -> DiscordHelpers.cs
        /// - poll_create_modal -> PollComponentHandlers.cs
        /// </summary>
        private Task HandleModalSubmitted(SocketModal modal)
        {
            // All modals now use attribute-based handlers via InteractionService
            return Task.CompletedTask;
        }

        /// <summary>
        /// Legacy button event handler - kept for any future non-attribute components.
        /// Poll buttons now use [ComponentInteraction] attribute handlers in PollComponentHandlers.cs
        /// </summary>
        private Task HandleButtonExecuted(SocketMessageComponent component)
        {
            // All poll components now use attribute-based handlers via InteractionService
            return Task.CompletedTask;
        }

        private async Task HandlePollVoteComponent(SocketMessageComponent component)
        {
            try
            {
                // Try to defer the interaction
                // Note: This can fail with "Unknown interaction" if the message was recently updated
                // but Discord often processes the defer successfully despite the error
                bool deferred = false;
                if (!component.HasResponded)
                {
                    try
                    {
                        await component.DeferAsync(ephemeral: true);
                        deferred = true;
                    }
                    catch (Discord.Net.HttpException ex) when (ex.DiscordCode.GetValueOrDefault() == (DiscordErrorCode)10062)
                    {
                        // Error 10062 typically means the defer actually succeeded on Discord's backend
                        // but the REST API hasn't synced yet. Always assume it succeeded and use FollowupAsync.
                        _logger.LogDebug("Received error 10062 on vote defer - assuming defer succeeded");
                        deferred = true;
                    }
                    catch (Discord.Net.HttpException ex)
                    {
                        _logger.LogError(ex, "Failed to defer poll vote (DiscordCode: {Code})", ex.DiscordCode);
                        throw;
                    }
                }

                // Parse custom ID: poll_vote~userId~pollId~optionId
                var parts = component.Data.CustomId.Split('~');
                if (parts.Length != 4 ||
                    !ulong.TryParse(parts[1], out var userId) ||
                    !long.TryParse(parts[2], out var pollId) ||
                    !long.TryParse(parts[3], out var optionId))
                {
                    _logger.LogWarning("Invalid poll vote CustomId format: {CustomId}", component.Data.CustomId);
                    var errorMsg = "❌ Invalid poll data.";
                    try
                    {
                        if (deferred)
                            await component.FollowupAsync(errorMsg, ephemeral: true);
                        else
                            await component.RespondAsync(errorMsg, ephemeral: true);
                    }
                    catch (Discord.Net.HttpException ex) when (ex.DiscordCode.GetValueOrDefault() == (DiscordErrorCode)40060)
                    {
                        await component.FollowupAsync(errorMsg, ephemeral: true);
                    }
                    return;
                }

                // Process vote
                var result = await ProcessPollVoteAsync(pollId, optionId, (long)component.User.Id, component.User.Username);

                // Update poll message
                await UpdatePollMessageAsync(pollId);

                // Send response
                try
                {
                    if (deferred)
                    {
                        await component.FollowupAsync(result, ephemeral: true);
                    }
                    else
                    {
                        await component.RespondAsync(result, ephemeral: true);
                    }
                }
                catch (Discord.Net.HttpException ex) when (ex.DiscordCode.GetValueOrDefault() == (DiscordErrorCode)40060)
                {
                    // Interaction already acknowledged - try followup instead
                    _logger.LogDebug("Interaction already acknowledged (40060), using followup");
                    await component.FollowupAsync(result, ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing poll vote");
                try
                {
                    await component.FollowupAsync("❌ An error occurred while processing your vote.", ephemeral: true);
                }
                catch
                {
                    _logger.LogWarning("Could not send error followup - interaction may have expired");
                }
            }
        }

        private async Task HandlePollCloseComponent(SocketMessageComponent component)
        {
            try
            {
                // Try to defer the interaction
                // Note: This can fail with "Unknown interaction" if the message was recently updated
                // but Discord often processes the defer successfully despite the error
                bool deferred = false;
                if (!component.HasResponded)
                {
                    try
                    {
                        await component.DeferAsync(ephemeral: true);
                        deferred = true;
                    }
                    catch (Discord.Net.HttpException ex) when (ex.DiscordCode.GetValueOrDefault() == (DiscordErrorCode)10062)
                    {
                        // Error 10062 typically means the defer actually succeeded on Discord's backend
                        // but the REST API hasn't synced yet. Always assume it succeeded and use FollowupAsync.
                        _logger.LogDebug("Received error 10062 on close defer - assuming defer succeeded");
                        deferred = true;
                    }
                    catch (Discord.Net.HttpException ex)
                    {
                        _logger.LogError(ex, "Failed to defer poll close (DiscordCode: {Code})", ex.DiscordCode);
                        throw;
                    }
                }

                // Parse custom ID: poll_close~creatorId~pollId
                var parts = component.Data.CustomId.Split('~');
                if (parts.Length != 3 ||
                    !long.TryParse(parts[1], out var creatorId) ||
                    !long.TryParse(parts[2], out var pollId))
                {
                    _logger.LogWarning("Invalid poll close CustomId format: {CustomId}", component.Data.CustomId);
                    var errorMsg = "❌ Invalid poll data.";
                    try
                    {
                        if (deferred)
                            await component.FollowupAsync(errorMsg, ephemeral: true);
                        else
                            await component.RespondAsync(errorMsg, ephemeral: true);
                    }
                    catch (Discord.Net.HttpException ex) when (ex.DiscordCode.GetValueOrDefault() == (DiscordErrorCode)40060)
                    {
                        await component.FollowupAsync(errorMsg, ephemeral: true);
                    }
                    return;
                }

                var (success, message) = await ClosePollAsync(pollId, (long)component.User.Id, component.Channel as SocketGuildChannel);

                // Send response
                try
                {
                    if (deferred)
                        await component.FollowupAsync(message, ephemeral: true);
                    else
                        await component.RespondAsync(message, ephemeral: true);
                }
                catch (Discord.Net.HttpException ex) when (ex.DiscordCode.GetValueOrDefault() == (DiscordErrorCode)40060)
                {
                    // Interaction already acknowledged - try followup instead
                    _logger.LogDebug("Interaction already acknowledged (40060), using followup");
                    await component.FollowupAsync(message, ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing poll");
                try
                {
                    await component.FollowupAsync("❌ An error occurred while closing the poll.", ephemeral: true);
                }
                catch
                {
                    _logger.LogWarning("Could not send error followup - interaction may have expired");
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
                    // Resolve channel/role/emoji placeholders to Discord format
                    var resolvedMessage = ResolveEntityPlaceholders(partingMessage.Trim(), guildInfo);

                    embed.Title = $"Parting message change for {guildInfo.Name}";
                    sb.AppendLine("New message:");
                    sb.AppendLine(resolvedMessage);

                    await using var greetingRepo = GetRepository<ServerGreeting>();
                    await greetingRepo.UpsertAsync(
                        findPredicate: g => g.DiscordGuildId == (long)modal.GuildId,
                        updateAction: greeting =>
                        {
                            greeting.PartingMessage = resolvedMessage;
                            greeting.SetById = (long)modal.User.Id;
                            greeting.SetByName = modal.User.Username;
                            greeting.TimeSet = DateTime.UtcNow;
                        },
                        createFactory: () => new ServerGreeting
                        {
                            DiscordGuildId = (long)modal.GuildId,
                            PartingMessage = resolvedMessage,
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
                    // Resolve channel/role/emoji placeholders to Discord format
                    var resolvedMessage = ResolveEntityPlaceholders(joiningMessage.Trim(), guildInfo);

                    embed.Title = $"Joining message change for {guildInfo.Name}";
                    sb.AppendLine("New message:");
                    sb.AppendLine(resolvedMessage);

                    await using var greetingRepo = GetRepository<ServerGreeting>();
                    await greetingRepo.UpsertAsync(
                        findPredicate: g => g.DiscordGuildId == (long)modal.GuildId,
                        updateAction: greeting =>
                        {
                            greeting.Greeting = resolvedMessage;
                            greeting.SetById = (long)modal.User.Id;
                            greeting.SetByName = modal.User.Username;
                            greeting.TimeSet = DateTime.UtcNow;
                        },
                        createFactory: () => new ServerGreeting
                        {
                            DiscordGuildId = (long)modal.GuildId,
                            Greeting = resolvedMessage,
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
                _logger.LogError(ex, "Error setting note for guild {GuildId}", guildInfo.Id);
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
                if (shouldGreet != null && shouldGreet.PartUsers == true)
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
                            if (string.IsNullOrEmpty(shouldGreet.PartingMessage))
                            {
                                sb.AppendLine($"Fine, be that way! :wave:");
                            }
                            else
                            {
                                var expandedParting = ExpandPlaceholders(shouldGreet.PartingMessage, user, guild);
                                sb.AppendLine(expandedParting);
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
                        if (string.IsNullOrEmpty(shouldGreet.Greeting))
                        {
                            sb.AppendLine($"Welcome them! :hugging:");
                            sb.AppendLine($"(or not, :shrug:)");
                        }
                        else
                        {
                            var expandedGreeting = ExpandPlaceholders(shouldGreet.Greeting, user);
                            sb.AppendLine(expandedGreeting);
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
        /// Expands placeholders in a greeting or parting message with actual user/server values.
        /// Supports: {user}, {username}, {user.tag}, {server}, {membercount}
        /// Channel and role placeholders should be pre-resolved to Discord format (<#ID>, <@&ID>) at save time.
        /// </summary>
        private string ExpandPlaceholders(string message, SocketGuildUser user)
        {
            if (string.IsNullOrEmpty(message)) return message;

            return message
                .Replace("{user}", user.Mention)
                .Replace("{username}", user.Username)
                .Replace("{user.tag}", $"{user.Username}#{user.Discriminator}")
                .Replace("{server}", user.Guild.Name)
                .Replace("{membercount}", user.Guild.MemberCount.ToString("N0"));
        }

        /// <summary>
        /// Expands placeholders for parting messages (user has left, so we only have SocketUser).
        /// </summary>
        private string ExpandPlaceholders(string message, SocketUser user, SocketGuild guild)
        {
            if (string.IsNullOrEmpty(message)) return message;

            return message
                .Replace("{user}", $"@{user.Username}")  // Can't mention a user who left
                .Replace("{username}", user.Username)
                .Replace("{user.tag}", $"{user.Username}#{user.Discriminator}")
                .Replace("{server}", guild.Name)
                .Replace("{membercount}", guild.MemberCount.ToString("N0"));
        }

        /// <summary>
        /// Resolves entity placeholders ({#channel-name}, {@role-name}, {:emoji-name:}) to Discord format.
        /// Called when saving messages from Discord modals or web API.
        /// </summary>
        private string ResolveEntityPlaceholders(string message, SocketGuild guild)
        {
            if (string.IsNullOrEmpty(message) || guild == null) return message;

            var result = message;

            // Resolve {#channel-name} to <#CHANNEL_ID>
            var channelRegex = new Regex(@"\{#([^}]+)\}");
            foreach (Match match in channelRegex.Matches(message))
            {
                var channelName = match.Groups[1].Value;
                var channel = guild.TextChannels.FirstOrDefault(c =>
                    c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));
                if (channel != null)
                {
                    result = result.Replace(match.Value, $"<#{channel.Id}>");
                }
            }

            // Resolve {@role-name} or {&role-name} to <@&ROLE_ID>
            var roleRegex = new Regex(@"\{[@&]([^}]+)\}");
            foreach (Match match in roleRegex.Matches(result))
            {
                var roleName = match.Groups[1].Value;
                var role = guild.Roles.FirstOrDefault(r =>
                    r.Name.Equals(roleName, StringComparison.OrdinalIgnoreCase));
                if (role != null)
                {
                    result = result.Replace(match.Value, $"<@&{role.Id}>");
                }
            }

            // Resolve {:emoji-name:} to <:name:ID> or <a:name:ID>
            var emojiRegex = new Regex(@"\{:([^:}]+):\}");
            foreach (Match match in emojiRegex.Matches(result))
            {
                var emojiName = match.Groups[1].Value;
                var emoji = guild.Emotes.FirstOrDefault(e =>
                    e.Name.Equals(emojiName, StringComparison.OrdinalIgnoreCase));
                if (emoji != null)
                {
                    var prefix = emoji.Animated ? "a" : "";
                    result = result.Replace(match.Value, $"<{prefix}:{emoji.Name}:{emoji.Id}>");
                }
            }

            return result;
        }

        private async Task HandlePollModal(SocketModal modal, List<SocketMessageComponentData> components)
        {
            try
            {
                // Extract form values
                var question = components.First(x => x.CustomId == "poll_question").Value?.Trim();
                var optionsText = components.FirstOrDefault(x => x.CustomId == "poll_options")?.Value?.Trim();
                var durationText = components.FirstOrDefault(x => x.CustomId == "poll_duration")?.Value?.Trim();
                var anonymousText = components.FirstOrDefault(x => x.CustomId == "poll_anonymous")?.Value?.Trim();

                // Validate question
                if (string.IsNullOrWhiteSpace(question))
                {
                    await modal.FollowupAsync("❌ Poll question cannot be empty.", ephemeral: true);
                    return;
                }

                // Parse options
                List<string> options;
                string pollType;

                if (string.IsNullOrWhiteSpace(optionsText))
                {
                    // Default to Yes/No poll
                    options = new List<string> { "Yes", "No" };
                    pollType = "YesNo";
                }
                else
                {
                    // Parse newline-separated options
                    options = optionsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(o => !string.IsNullOrWhiteSpace(o))
                        .Take(25) // Discord limit: 25 buttons max (5 rows x 5 buttons)
                        .ToList();

                    if (options.Count < 2)
                    {
                        await modal.FollowupAsync("❌ Poll must have at least 2 options.", ephemeral: true);
                        return;
                    }

                    pollType = "SingleChoice";
                }

                // Parse duration
                DateTime? expiresAt = null;
                if (!string.IsNullOrWhiteSpace(durationText))
                {
                    expiresAt = ParsePollDuration(durationText);
                    if (!expiresAt.HasValue)
                    {
                        await modal.FollowupAsync("❌ Invalid duration. Use 1h-720h, 1d-30d, or 1w-4w (max 30 days).", ephemeral: true);
                        return;
                    }
                }

                // Create poll in database using UnitOfWork for atomic operation
                // All repositories share the same context, ensuring poll + options save together
                Database.Poll poll;
                await using (var uow = new UnitOfWork(_scopeFactory))
                {
                    var settingsRepo = uow.Repository<Database.ServerPollSettings>();
                    var pollRepo = uow.Repository<Database.Poll>();
                    var optionRepo = uow.Repository<Database.PollOption>();

                    // Get server defaults
                    var serverSettings = await settingsRepo.FirstOrDefaultAsync(
                        s => s.DiscordGuildId == (long)modal.GuildId);

                    // Determine anonymous setting (modal override > server default > false)
                    bool isAnonymous;
                    if (!string.IsNullOrWhiteSpace(anonymousText))
                    {
                        isAnonymous = anonymousText.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        isAnonymous = serverSettings?.DefaultAnonymous ?? false;
                    }

                    // Inherit role restrictions from server defaults
                    var allowedRoleIds = serverSettings?.DefaultAllowedRoleIds;

                    // Create poll entity
                    var newPoll = new Database.Poll
                    {
                        Question = question,
                        PollType = pollType,
                        AllowVoteChange = true,
                        IsAnonymous = isAnonymous,
                        IsClosed = false,
                        CreatedAt = DateTime.UtcNow,
                        ExpiresAt = expiresAt,
                        CreatedById = (long)modal.User.Id,
                        CreatedByName = modal.User.Username,
                        GuildId = (long)modal.GuildId,
                        ChannelId = (long)modal.Channel.Id,
                        MessageId = 0, // Will be updated after posting message
                        AllowedRoleIds = allowedRoleIds
                    };

                    await pollRepo.AddAsync(newPoll);

                    // Add options (EF Core will assign PollId after SaveChanges due to navigation)
                    for (int i = 0; i < options.Count; i++)
                    {
                        var option = new Database.PollOption
                        {
                            Poll = newPoll, // Use navigation property for proper FK assignment
                            OptionText = options[i],
                            DisplayOrder = i,
                            Emote = GetPollEmote(i)
                        };
                        await optionRepo.AddAsync(option);
                    }

                    // Single SaveChanges - poll and options saved atomically
                    await uow.SaveChangesAsync();

                    // Reload with options using Context directly for Include support
                    poll = await uow.Context.Set<Database.Poll>()
                        .Include(p => p.PollOptions)
                        .FirstOrDefaultAsync(p => p.Id == newPoll.Id);
                }

                if (poll == null)
                {
                    await modal.FollowupAsync("❌ Failed to create poll.", ephemeral: true);
                    return;
                }

                // Post poll message to channel
                var embed = BuildPollEmbed(poll);
                var pollComponents = BuildPollComponents(poll, modal.User.Id);

                var guild = _client.GetGuild((ulong)modal.GuildId);
                var channel = guild?.GetChannel((ulong)modal.Channel.Id) as ISocketMessageChannel;
                if (channel == null)
                {
                    // Clean up orphaned poll
                    await CleanupOrphanedPollAsync(poll.Id);
                    await modal.FollowupAsync("❌ Could not access channel.", ephemeral: true);
                    return;
                }

                IUserMessage message;
                try
                {
                    message = await channel.SendMessageAsync(embed: embed.Build(), components: pollComponents.Build());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to post poll message, cleaning up poll {PollId}", poll.Id);
                    await CleanupOrphanedPollAsync(poll.Id);
                    await modal.FollowupAsync("❌ Failed to post poll message. Please try again.", ephemeral: true);
                    return;
                }

                // Update poll with message ID
                await using (var updateRepo = new Repository<Database.Poll>(_scopeFactory))
                {
                    var pollToUpdate = await updateRepo.Query.FirstOrDefaultAsync(p => p.Id == poll.Id);
                    if (pollToUpdate != null)
                    {
                        pollToUpdate.MessageId = (long)message.Id;
                        updateRepo.Update(pollToUpdate);
                        await updateRepo.SaveChangesAsync();
                    }
                }

                _logger.LogInformation("Poll created: {PollId} by {UserId} in {GuildId}",
                    poll.Id, modal.User.Id, modal.GuildId);

                await modal.FollowupAsync($"✅ Poll created successfully! Check <#{modal.Channel.Id}>", ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling poll modal");
                try
                {
                    await modal.FollowupAsync("❌ An error occurred while creating the poll.", ephemeral: true);
                }
                catch
                {
                    // Ignore followup errors
                }
            }
        }

        private async Task CleanupOrphanedPollAsync(long pollId)
        {
            try
            {
                await using var pollRepo = new Repository<Database.Poll>(_scopeFactory);
                await using var optionRepo = new Repository<Database.PollOption>(_scopeFactory);

                // Delete options first (foreign key constraint)
                var options = await optionRepo.Query.Where(o => o.PollId == pollId).ToListAsync();
                foreach (var option in options)
                {
                    optionRepo.Delete(option);
                }
                await optionRepo.SaveChangesAsync();

                // Delete the poll
                var orphan = await pollRepo.Query.FirstOrDefaultAsync(p => p.Id == pollId);
                if (orphan != null)
                {
                    pollRepo.Delete(orphan);
                    await pollRepo.SaveChangesAsync();
                }

                _logger.LogInformation("Cleaned up orphaned poll {PollId}", pollId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup orphaned poll {PollId}", pollId);
            }
        }

        private DateTime? ParseDuration(string duration)
        {
            if (string.IsNullOrWhiteSpace(duration))
                return null;

            var match = DurationRegex.Match(duration.Trim().ToLower());
            if (!match.Success)
                return null;

            var value = int.Parse(match.Groups[1].Value);
            var unit = match.Groups[2].Value;

            // Calculate total hours to enforce 30-day maximum (720 hours)
            var totalHours = unit switch
            {
                "h" => value,
                "d" => value * 24,
                "w" => value * 24 * 7,
                _ => 0
            };

            if (totalHours > 720 || totalHours <= 0)
                return null;

            return unit switch
            {
                "h" => DateTime.UtcNow.AddHours(value),
                "d" => DateTime.UtcNow.AddDays(value),
                "w" => DateTime.UtcNow.AddDays(value * 7),
                _ => null
            };
        }

        private DateTime? ParsePollDuration(string duration)
        {
            return ParseDuration(duration); // Same logic
        }

        private string GetPollEmote(int index)
        {
            var emotes = new[] { "1️⃣", "2️⃣", "3️⃣", "4️⃣", "5️⃣", "6️⃣", "7️⃣", "8️⃣", "9️⃣", "🔟",
                               "🇦", "🇧", "🇨", "🇩", "🇪", "🇫", "🇬", "🇭", "🇮", "🇯" };
            return index < emotes.Length ? emotes[index] : "▪️";
        }

        private EmbedBuilder BuildPollEmbed(Database.Poll poll)
        {
            var embed = new EmbedBuilder()
                .WithTitle($"📊 {poll.Question}")
                .WithColor(poll.IsClosed ? Color.Red : Color.Blue)
                .WithFooter($"Created by {poll.CreatedByName} • 0 votes")
                .WithTimestamp(poll.CreatedAt);

            // Add fields for each option with initial zero counts
            foreach (var option in poll.PollOptions.OrderBy(o => o.DisplayOrder))
            {
                var bar = new string('░', 20);
                var emote = !string.IsNullOrEmpty(option.Emote) ? option.Emote + " " : "";
                embed.AddField($"{emote}{option.OptionText}",
                    $"`{bar}` 0.0% (0 votes)",
                    inline: false);
            }

            // Add info fields
            if (poll.ExpiresAt.HasValue && !poll.IsClosed)
            {
                embed.AddField("Expires", $"<t:{new DateTimeOffset(poll.ExpiresAt.Value).ToUnixTimeSeconds()}:R>", inline: true);
            }

            if (poll.IsAnonymous)
            {
                embed.AddField("Voting", "🔒 Anonymous", inline: true);
            }

            if (!string.IsNullOrEmpty(poll.AllowedRoleIds))
            {
                var roleIds = poll.AllowedRoleIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var roleMentions = roleIds.Select(id => $"<@&{id}>").Take(5).ToList();
                var roleText = string.Join(", ", roleMentions);
                if (roleIds.Length > 5)
                    roleText += $" +{roleIds.Length - 5} more";
                embed.AddField("Restricted to", roleText, inline: true);
            }

            return embed;
        }

        private ComponentBuilder BuildPollComponents(Database.Poll poll, ulong userId)
        {
            var builder = new ComponentBuilder();

            if (poll.IsClosed)
            {
                return builder;
            }

            var options = poll.PollOptions.OrderBy(o => o.DisplayOrder).ToList();

            // Build vote buttons (up to 25 options across 5 rows)
            if (options.Count <= 25)
            {
                int currentRow = 0;
                int buttonsInRow = 0;

                foreach (var option in options)
                {
                    var customId = $"{ModalConstants.PollVotePrefix}{userId}~{poll.Id}~{option.Id}";
                    var button = new ButtonBuilder()
                        .WithLabel(TruncatePollLabel(option.OptionText, 80))
                        .WithCustomId(customId)
                        .WithStyle(ButtonStyle.Primary);

                    if (!string.IsNullOrEmpty(option.Emote))
                    {
                        try
                        {
                            button.WithEmote(new Emoji(option.Emote));
                        }
                        catch
                        {
                            // Ignore invalid emotes
                        }
                    }

                    builder.WithButton(button, row: currentRow);

                    buttonsInRow++;
                    if (buttonsInRow >= 5)
                    {
                        currentRow++;
                        buttonsInRow = 0;
                    }
                }

                // Add close button on next available row (if space)
                if (currentRow < 4 || (currentRow == 4 && buttonsInRow == 0))
                {
                    var closeRow = buttonsInRow > 0 ? currentRow + 1 : currentRow;
                    var closeButton = new ButtonBuilder()
                        .WithLabel("Close Poll")
                        .WithCustomId($"{ModalConstants.PollClosePrefix}{poll.CreatedById}~{poll.Id}")
                        .WithStyle(ButtonStyle.Danger)
                        .WithEmote(new Emoji("🔒"));
                    builder.WithButton(closeButton, row: closeRow);
                }
            }

            return builder;
        }

        private string TruncatePollLabel(string label, int maxLength)
        {
            if (label.Length <= maxLength)
                return label;
            return label.Substring(0, maxLength - 3) + "...";
        }

        private async Task<string> ProcessPollVoteAsync(long pollId, long optionId, long userId, string userName)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var poll = await db.Polls
                .Include(p => p.PollOptions)
                .FirstOrDefaultAsync(p => p.Id == pollId);

            if (poll == null)
                return "❌ Poll not found.";

            if (poll.IsClosed)
                return "❌ This poll is closed.";

            if (poll.ExpiresAt.HasValue && DateTime.UtcNow > poll.ExpiresAt.Value)
                return "❌ This poll has expired.";

            // Check role restrictions
            if (!string.IsNullOrEmpty(poll.AllowedRoleIds))
            {
                var allowedRoles = poll.AllowedRoleIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(r => long.TryParse(r.Trim(), out var id) ? id : 0)
                    .Where(id => id > 0)
                    .ToHashSet();

                var guild = _client.GetGuild((ulong)poll.GuildId);
                var member = guild?.GetUser((ulong)userId);

                if (member == null || !member.Roles.Any(r => allowedRoles.Contains((long)r.Id)))
                    return "❌ You don't have permission to vote on this poll.";
            }

            // Check if option exists
            if (!poll.PollOptions.Any(o => o.Id == optionId))
                return "❌ Invalid option.";

            // Use transaction to prevent race conditions in vote changes
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                try
                {
                    // Check existing votes within transaction
                    var existingVotes = await db.PollVotes
                        .Where(v => v.PollId == pollId && v.UserId == userId)
                        .ToListAsync();

                    if (poll.PollType == "SingleChoice" || poll.PollType == "YesNo")
                    {
                        if (existingVotes.Any())
                        {
                            if (!poll.AllowVoteChange)
                            {
                                await transaction.RollbackAsync();
                                return "❌ You've already voted and cannot change your vote.";
                            }

                            // Remove old votes
                            db.PollVotes.RemoveRange(existingVotes);
                        }
                    }
                    else if (poll.PollType == "MultipleChoice")
                    {
                        // Check if already voted for this option
                        var existingVote = existingVotes.FirstOrDefault(v => v.OptionId == optionId);
                        if (existingVote != null)
                        {
                            // Toggle off
                            db.PollVotes.Remove(existingVote);
                            await db.SaveChangesAsync();
                            await transaction.CommitAsync();
                            return "✅ Vote removed.";
                        }
                    }

                    // Add new vote
                    var newVote = new Database.PollVote
                    {
                        PollId = pollId,
                        OptionId = optionId,
                        UserId = userId,
                        UserName = userName,
                        VotedAt = DateTime.UtcNow
                    };

                    await db.PollVotes.AddAsync(newVote);
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    return "✅ Vote recorded!";
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });
        }

        private async Task UpdatePollMessageAsync(long pollId)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var poll = await db.Polls
                .Include(p => p.PollOptions)
                .FirstOrDefaultAsync(p => p.Id == pollId);

            if (poll == null)
                return;

            var votes = await db.PollVotes
                .Where(vote => vote.PollId == pollId)
                .ToListAsync();

            var totalVotes = votes.Count;
            var embed = new EmbedBuilder()
                .WithTitle($"📊 {poll.Question}")
                .WithColor(poll.IsClosed ? Color.Red : Color.Blue)
                .WithFooter($"Created by {poll.CreatedByName} • {totalVotes} vote{(totalVotes != 1 ? "s" : "")}")
                .WithTimestamp(poll.CreatedAt);

            // Add fields for each option with vote counts
            foreach (var option in poll.PollOptions.OrderBy(o => o.DisplayOrder))
            {
                var optionVotes = votes.Count(v => v.OptionId == option.Id);
                var percentage = totalVotes > 0 ? (optionVotes * 100.0 / totalVotes) : 0;
                var barLength = (int)(percentage / 5); // 20 chars max
                var bar = new string('█', Math.Min(barLength, 20)) + new string('░', Math.Max(20 - barLength, 0));

                var emote = !string.IsNullOrEmpty(option.Emote) ? option.Emote + " " : "";
                embed.AddField($"{emote}{option.OptionText}",
                    $"`{bar}` {percentage:F1}% ({optionVotes} vote{(optionVotes != 1 ? "s" : "")})",
                    inline: false);
            }

            // Add info fields
            if (poll.ExpiresAt.HasValue && !poll.IsClosed)
            {
                embed.AddField("Expires", $"<t:{new DateTimeOffset(poll.ExpiresAt.Value).ToUnixTimeSeconds()}:R>", inline: true);
            }

            if (poll.IsAnonymous)
            {
                embed.AddField("Voting", "🔒 Anonymous", inline: true);
            }

            if (!string.IsNullOrEmpty(poll.AllowedRoleIds))
            {
                var roleIds = poll.AllowedRoleIds.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var roleMentions = roleIds.Select(id => $"<@&{id}>").Take(5).ToList();
                var roleText = string.Join(", ", roleMentions);
                if (roleIds.Length > 5)
                    roleText += $" +{roleIds.Length - 5} more";
                embed.AddField("Restricted to", roleText, inline: true);
            }

            if (poll.IsClosed)
            {
                embed.WithDescription("🔒 **This poll is closed**");
            }

            var components = BuildPollComponents(poll, 0); // Use 0 as placeholder

            var guild = _client.GetGuild((ulong)poll.GuildId);
            var channel = guild?.GetChannel((ulong)poll.ChannelId) as IMessageChannel;
            if (channel == null)
                return;

            var message = await channel.GetMessageAsync((ulong)poll.MessageId);
            if (message is IUserMessage userMessage)
            {
                await userMessage.ModifyAsync(msg =>
                {
                    msg.Embed = embed.Build();
                    msg.Components = components.Build();
                });
            }
        }

        private async Task<(bool success, string message)> ClosePollAsync(long pollId, long userId, SocketGuildChannel channel)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();

            var poll = await db.Polls
                .Include(p => p.PollOptions)
                .Include(p => p.PollVotes)
                .AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == pollId);

            if (poll == null)
                return (false, "❌ Poll not found.");

            if (poll.IsClosed)
                return (false, "❌ Poll is already closed.");

            // Check permissions - only creator or moderators can close
            var guildUser = channel?.Guild?.GetUser((ulong)userId);
            var isCreator = poll.CreatedById == userId;
            var isModerator = guildUser?.GuildPermissions.ManageMessages ?? false;

            if (!isCreator && !isModerator)
                return (false, "❌ Only the poll creator or moderators can close this poll.");

            poll.IsClosed = true;
            poll.ClosedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            // Update poll message
            await UpdatePollMessageAsync(pollId);

            // Post poll results
            await PostPollResultsAsync(poll, guildUser?.Username ?? "Unknown", db);

            var totalVotes = poll.PollVotes.Count;
            return (true, $"✅ Poll closed successfully. Total votes: {totalVotes}");
        }

        private async Task PostPollResultsAsync(Database.Poll poll, string closedBy, NinjaBotEntities db)
        {
            try
            {
                // Get server poll settings
                var settings = await db.ServerPollSettings
                    .FirstOrDefaultAsync(s => s.DiscordGuildId == poll.GuildId);

                // Determine target channel
                var targetChannelId = settings?.ResultsChannelId ?? poll.ChannelId;

                var guild = _client.GetGuild((ulong)poll.GuildId);
                if (guild == null)
                {
                    _logger.LogWarning("Guild {GuildId} not found for poll results {PollId}", poll.GuildId, poll.Id);
                    return;
                }

                var channel = guild.GetTextChannel((ulong)targetChannelId);
                if (channel == null)
                {
                    _logger.LogWarning("Channel {ChannelId} not found for poll results {PollId}", targetChannelId, poll.Id);
                    return;
                }

                // Build results embed
                var resultsBuilder = new PollResultsBuilder();
                var options = poll.PollOptions?.ToList() ?? new List<Database.PollOption>();
                var votes = poll.PollVotes?.ToList() ?? new List<Database.PollVote>();
                var embed = resultsBuilder.BuildResultsEmbed(poll, options, votes, closedBy: closedBy, wasExpired: false);

                // Build voter mentions if enabled
                string? content = null;
                if (settings?.MentionVotersOnClose == true && !poll.IsAnonymous)
                {
                    content = resultsBuilder.BuildVoterMentions(votes, poll.IsAnonymous);
                }

                // Send results message as a reply to the original poll
                var messageReference = new MessageReference(
                    messageId: (ulong)poll.MessageId,
                    channelId: (ulong)poll.ChannelId,
                    guildId: (ulong)poll.GuildId,
                    failIfNotExists: false);

                await channel.SendMessageAsync(
                    text: string.IsNullOrEmpty(content) ? null : content,
                    embed: embed,
                    messageReference: messageReference,
                    allowedMentions: AllowedMentions.All);

                _logger.LogInformation("Posted poll results for poll {PollId} to channel {ChannelId}", poll.Id, targetChannelId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error posting poll results for poll {PollId}", poll.Id);
            }
        }

        /// <summary>
        /// Cleans up a handled interaction after a delay to prevent duplicate handling
        /// </summary>
        private async Task CleanupInteractionAsync(ulong interactionId)
        {
            try
            {
                await Task.Delay(1000);
                _handledInteractions.TryRemove(interactionId, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up interaction {Id}", interactionId);
            }
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
                _client.ModalSubmitted -= HandleModalSubmitted;
                _client.ButtonExecuted -= HandleButtonExecuted;

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
