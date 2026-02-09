using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Models.Help;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Misc
{
    public class HelpCommands : InteractionModuleBase<ShardedInteractionContext>
    {
        private readonly ILogger<HelpCommands> _logger;
        private readonly IConfigurationRoot _config;
        private readonly HelpContentProvider _helpProvider;

        public HelpCommands(
            ILogger<HelpCommands> logger,
            IConfigurationRoot config,
            HelpContentProvider helpProvider)
        {
            _logger = logger;
            _config = config;
            _helpProvider = helpProvider;
        }

        private HelpContent? HelpContent => _helpProvider.GetHelpContent();
        private string? AvatarUrl => Context.Client.CurrentUser.GetAvatarUrl();
        private string? Version => HelpContent?.Metadata?.Version;
        private bool IsOwnerUser => IsOwner(Context.User.Id);

        private Func<string, bool> PermissionCheck =>
            perm => HasPermissionForCommand(Context.User as SocketGuildUser, perm);

        [SlashCommand("help", "Browse NinjaBot commands by category")]
        public async Task Help()
        {
            try
            {
                if (HelpContent == null)
                {
                    await RespondAsync("Help system is not available. Please contact the bot owner.", ephemeral: true);
                    return;
                }

                var embed = HelpViewBuilder.BuildWelcomeEmbed(AvatarUrl, Version);
                var components = HelpViewBuilder.BuildCategorySelectMenu(HelpContent, IsOwnerUser, PermissionCheck);

                await RespondAsync(embed: embed.Build(), components: components.Build(), ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Help command");
                await RespondAsync("An error occurred while loading help. Please try again.", ephemeral: true);
            }
        }

        [ComponentInteraction("help_category_select")]
        public async Task HandleCategorySelection(string[] selections)
        {
            try
            {
                await DeferAsync();

                string categoryId = selections[0];

                if (categoryId == "welcome")
                {
                    var welcomeEmbed = HelpViewBuilder.BuildWelcomeEmbed(AvatarUrl, Version);
                    var components = HelpViewBuilder.BuildCategorySelectMenu(HelpContent, IsOwnerUser, PermissionCheck);

                    await ModifyOriginalResponseAsync(msg =>
                    {
                        msg.Embed = welcomeEmbed.Build();
                        msg.Components = components.Build();
                    });
                    return;
                }

                var embed = HelpViewBuilder.BuildCategoryEmbed(HelpContent, categoryId, 0, AvatarUrl, Version, PermissionCheck);
                var categoryComponents = HelpViewBuilder.BuildCategoryComponents(HelpContent, categoryId, 0, Context.User.Id, IsOwnerUser, PermissionCheck);

                await ModifyOriginalResponseAsync(msg =>
                {
                    msg.Embed = embed.Build();
                    msg.Components = categoryComponents.Build();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling category selection");
            }
        }

        [ComponentInteraction("help_first~*~*~*")]
        public async Task HandleHelpFirst(string userIdStr, string categoryId, string currentPageStr)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            await DeferAsync(ephemeral: true);

            var embed = HelpViewBuilder.BuildCategoryEmbed(HelpContent, categoryId, 0, AvatarUrl, Version, PermissionCheck);
            var components = HelpViewBuilder.BuildCategoryComponents(HelpContent, categoryId, 0, Context.User.Id, IsOwnerUser, PermissionCheck);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("help_prev~*~*~*")]
        public async Task HandleHelpPrevious(string userIdStr, string categoryId, string currentPageStr)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(currentPageStr, out var currentPage))
            {
                await RespondAsync("Invalid page data.", ephemeral: true);
                return;
            }

            var targetPage = Math.Max(0, currentPage - 1);

            await DeferAsync(ephemeral: true);

            var embed = HelpViewBuilder.BuildCategoryEmbed(HelpContent, categoryId, targetPage, AvatarUrl, Version, PermissionCheck);
            var components = HelpViewBuilder.BuildCategoryComponents(HelpContent, categoryId, targetPage, Context.User.Id, IsOwnerUser, PermissionCheck);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("help_next~*~*~*")]
        public async Task HandleHelpNext(string userIdStr, string categoryId, string currentPageStr)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(currentPageStr, out var currentPage))
            {
                await RespondAsync("Invalid page data.", ephemeral: true);
                return;
            }

            var targetPage = currentPage + 1;

            await DeferAsync(ephemeral: true);

            var embed = HelpViewBuilder.BuildCategoryEmbed(HelpContent, categoryId, targetPage, AvatarUrl, Version, PermissionCheck);
            var components = HelpViewBuilder.BuildCategoryComponents(HelpContent, categoryId, targetPage, Context.User.Id, IsOwnerUser, PermissionCheck);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction("help_last~*~*~*~*")]
        public async Task HandleHelpLast(string userIdStr, string categoryId, string currentPageStr, string totalPagesStr)
        {
            if (!ulong.TryParse(userIdStr, out var userId) || Context.User.Id != userId)
            {
                await RespondAsync("This pagination belongs to another user.", ephemeral: true);
                return;
            }

            if (!int.TryParse(totalPagesStr, out var totalPages))
            {
                await RespondAsync("Invalid page data.", ephemeral: true);
                return;
            }

            var targetPage = totalPages - 1;

            await DeferAsync(ephemeral: true);

            var embed = HelpViewBuilder.BuildCategoryEmbed(HelpContent, categoryId, targetPage, AvatarUrl, Version, PermissionCheck);
            var components = HelpViewBuilder.BuildCategoryComponents(HelpContent, categoryId, targetPage, Context.User.Id, IsOwnerUser, PermissionCheck);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        private bool HasPermissionForCommand(SocketGuildUser user, string permission)
        {
            if (user == null) return permission == "public";

            return permission switch
            {
                "public" => true,
                "owner" => IsOwner(user.Id),
                "administrator" => user.GuildPermissions.Administrator,
                "moderator" => user.GuildPermissions.KickMembers ||
                               user.GuildPermissions.BanMembers ||
                               user.GuildPermissions.Administrator,
                "manage_messages" => user.GuildPermissions.ManageMessages ||
                                    user.GuildPermissions.Administrator,
                _ => false
            };
        }

        private bool IsOwner(ulong userId)
        {
            var ownerId = _config.GetValue<ulong>("OwnerId");
            return userId == ownerId;
        }

        [SlashCommand("regenerate-help", "Regenerate help-commands.json by scanning all slash commands")]
        [RequireOwner]
        public async Task RegenerateHelp()
        {
            await DeferAsync(ephemeral: true);

            try
            {
                _helpProvider.RegenerateHelpContent();

                var content = HelpContent;
                var commandCount = content?.Metadata?.TotalCommands ?? 0;
                var categoryCount = content?.Categories?.Count ?? 0;

                await FollowupAsync(
                    $"✅ Successfully regenerated help-commands.json!\n\n" +
                    $"**Commands found:** {commandCount}\n" +
                    $"**Categories:** {categoryCount}\n" +
                    $"**Last updated:** {content?.Metadata?.LastUpdated ?? "unknown"}",
                    ephemeral: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error regenerating help file");
                await FollowupAsync($"❌ Error regenerating help file: {ex.Message}", ephemeral: true);
            }
        }
    }
}
