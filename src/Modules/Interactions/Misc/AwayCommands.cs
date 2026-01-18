using System;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;

namespace NinjaBotCore.Modules.Interactions.Away
{
    public class AwayCommands : NinjaBotBaseModule
    {
        private readonly ILogger _logger;

        // Event handling is now done by AwaySystemService
        // This module only handles slash commands
        public AwayCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<AwayCommands> logger)
            : base(scopeFactory)
        {
            _logger = logger;
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

                await using var awayRepo = GetRepository<AwaySystem>();
                var existing = await awayRepo.FirstOrDefaultAsync(a => a.UserId == (long)user.Id);

                if (existing != null && existing.Status == true)
                {
                    sb.AppendLine($"You're already away, **{userMentionName}**!");
                }
                else
                {
                    sb.AppendLine($"Marking you as away, **{userMentionName}**, with the message: *{message}*");

                    await awayRepo.UpsertAsync(
                        findPredicate: a => a.UserId == (long)user.Id,
                        updateAction: away =>
                        {
                            away.Status = true;
                            away.Message = message;
                            away.TimeAway = DateTime.UtcNow;
                            away.UserName = userName;
                        },
                        createFactory: () => new AwaySystem
                        {
                            UserId = (long)user.Id,
                            UserName = userName,
                            Status = true,
                            Message = message,
                            TimeAway = DateTime.UtcNow
                        });
                    await awayRepo.SaveChangesAsync();
                }
                await RespondAsync(sb.ToString(), ephemeral: true);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Database error in away command for user {UserId}", Context.User.Id);
                await RespondAsync("Failed to update away status in database. Please try again.", ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in away command for user {UserId}", Context.User.Id);
                await RespondAsync("An unexpected error occurred. Please try again later.", ephemeral: true);
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

                await using var awayRepo = GetRepository<AwaySystem>();
                var existing = await awayRepo.FirstOrDefaultAsync(a => a.UserId == (long)user.Id);

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
                        findPredicate: a => a.UserId == (long)user.Id,
                        updateAction: away =>
                        {
                            away.Status = false;
                            away.Message = string.Empty;
                            away.UserName = userName;
                        },
                        createFactory: () => new AwaySystem
                        {
                            UserId = (long)user.Id,
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
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Database error in back command for user {UserId}", Context.User.Id);
                await RespondAsync("Failed to update back status in database. Please try again.", ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in back command for user {UserId}", Context.User.Id);
                await RespondAsync("An unexpected error occurred. Please try again later.", ephemeral: true);
            }
        }

        [SlashCommand("set-back-forced", "force a user as being back from away")]
        [RequireUserPermission(GuildPermission.KickMembers)]
        public async Task SetBack(IGuildUser user)
        {
            await SetBack(forced: true, forceUser: user);
        }
    }
}
