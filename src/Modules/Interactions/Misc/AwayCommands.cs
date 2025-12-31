using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Modules.Away;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Repositories;

namespace NinjaBotCore.Modules.Interactions.Away
{
    public class AwayCommands : NinjaBotBaseModule
    {
        private static bool _isLinked = false;
        private static DiscordShardedClient _client;
        private readonly ILogger _logger;

        public AwayCommands(IServiceScopeFactory scopeFactory, DiscordShardedClient client, ILogger<AwayCommands> logger)
            : base(scopeFactory)
        {
            _logger = logger;
            if (!_isLinked)
            {
                client.MessageReceived += AwayMentionFinder;
                _logger.LogInformation("Hooked into MessageReceived for Away module.");
            }
            _isLinked = true;
            if (_client == null)
            {
                _client = client;
            }
        }

        [SlashCommand("away", "set yourself as away, replying to @mentions of you")]
        public async Task SetAway(string input)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                var message = string.IsNullOrEmpty(input) ? "No message set!" : input;
                var user = Context.User;
                string userName = user.Username;
                string userMentionName = user.Mention;

                var awayRepo = GetRepository<AwaySystem>();
                var existing = await awayRepo.FirstOrDefaultAsync(a => a.UserName == userName);

                if (existing != null && existing.Status == true)
                {
                    sb.AppendLine($"You're already away, **{userMentionName}**!");
                }
                else
                {
                    sb.AppendLine($"Marking you as away, **{userMentionName}**, with the message: *{message}*");

                    await awayRepo.UpsertAsync(
                        findPredicate: a => a.UserName == userName,
                        updateAction: away =>
                        {
                            away.Status = true;
                            away.Message = message;
                            away.TimeAway = DateTime.UtcNow;
                        },
                        createFactory: () => new AwaySystem
                        {
                            UserName = userName,
                            Status = true,
                            Message = message,
                            TimeAway = DateTime.UtcNow
                        });
                    await awayRepo.SaveChangesAsync();
                }
                await RespondAsync(sb.ToString(), ephemeral: true);
            }
            catch (Exception ex)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Something went wrong setting you away :(");
                _logger.LogError($"Away command error {ex.Message}");
                await RespondAsync(sb.ToString(), ephemeral: true);
            }
        }

        [SlashCommand("back", "set yourself as back from being away")]
        public async Task SetBack(bool forced = false, IGuildUser forceUser = null)
        {
            try
            {
                var user = forced ? forceUser : Context.User as IGuildUser;
                StringBuilder sb = new StringBuilder();
                string userName = user.Username;
                string userMentionName = user.Mention;

                var awayRepo = GetRepository<AwaySystem>();
                var existing = await awayRepo.FirstOrDefaultAsync(a => a.UserName == userName);

                if (existing == null || existing.Status != true)
                {
                    sb.AppendLine($"You're not even away yet, **{userMentionName}**");
                }
                else
                {
                    string awayDuration = string.Empty;
                    if (existing.TimeAway.HasValue)
                    {
                        var awayTime = DateTime.UtcNow - existing.TimeAway;
                        if (awayTime.HasValue)
                        {
                            awayDuration = $"**{awayTime.Value.Days}** days, **{awayTime.Value.Hours}** hours, **{awayTime.Value.Minutes}** minutes, and **{awayTime.Value.Seconds}** seconds";
                        }
                    }

                    await awayRepo.UpsertAsync(
                        findPredicate: a => a.UserName == userName,
                        updateAction: away =>
                        {
                            away.Status = false;
                            away.Message = string.Empty;
                        },
                        createFactory: () => new AwaySystem
                        {
                            UserName = userName,
                            Status = false,
                            Message = string.Empty
                        });
                    await awayRepo.SaveChangesAsync();

                    if (forced)
                    {
                        sb.AppendLine($"You're now set as back **{userMentionName}** (forced by: **{Context.User.Username}**)!");
                    }
                    else
                    {
                        sb.AppendLine($"You're now set as back, **{userMentionName}**!");
                    }
                    sb.AppendLine($"You were away for: [{awayDuration}]");
                }
                await RespondAsync(sb.ToString(), ephemeral: true);
            }
            catch (Exception ex)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Something went wrong marking you as back :(");
                _logger.LogError($"Back command error {ex.Message}");
                await RespondAsync(sb.ToString());
            }
        }

        [SlashCommand("set-back-forced", "force a user as being back from away")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task SetBack(IGuildUser user)
        {
            await SetBack(forced: true, forceUser: user);
        }

        private async Task AwayMentionFinder(SocketMessage messageDetails)
        {
            await Task.Run(async () =>
            {
                var message = messageDetails as SocketUserMessage;
                if (!messageDetails.Author.IsBot)
                {
                    var userMentioned = messageDetails.MentionedUsers.ToList();
                    if (userMentioned != null)
                    {
                        foreach (var user in userMentioned)
                        {
                            var awayRepo = GetRepository<AwaySystem>();
                            var awayUser = await awayRepo.FirstOrDefaultAsync(a => a.UserName == user.Username);

                            if (awayUser != null && awayUser.Status == true)
                            {
                                string awayDuration = string.Empty;
                                if (awayUser.TimeAway.HasValue)
                                {
                                    var awayTime = DateTime.UtcNow - awayUser.TimeAway;
                                    if (awayTime.HasValue)
                                    {
                                        awayDuration = $"**{awayTime.Value.Days}** days, **{awayTime.Value.Hours}** hours, **{awayTime.Value.Minutes}** minutes, and **{awayTime.Value.Seconds}** seconds";
                                    }
                                }

                                _logger.LogInformation($"Mentioned user {user.Username} -> {awayUser.UserName} -> {awayUser.Status}");

                                SocketGuild guild = (message.Channel as SocketGuildChannel)?.Guild;
                                EmbedBuilder embed = new EmbedBuilder();
                                embed.WithColor(new Color(0, 71, 171));

                                if (!string.IsNullOrWhiteSpace(guild?.IconUrl))
                                {
                                    embed.ThumbnailUrl = user.GetAvatarUrl();
                                }

                                embed.Title = $":clock: {awayUser.UserName} is away! :clock:";
                                embed.Description = $"Since: **{awayUser.TimeAway}\n**Duration: {awayDuration}\nMessage: {awayUser.Message}";
                                await messageDetails.Channel.SendMessageAsync("", false, embed.Build());
                            }
                        }
                    }
                }
            });
        }
    }
}
