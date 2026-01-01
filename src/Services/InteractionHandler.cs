using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Services.ErrorHandling;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace NinjaBotCore.Services
{
    public class InteractionHandler
    {
        private readonly DiscordShardedClient _client;
        private readonly InteractionService _handler;
        private readonly IServiceProvider _services;
        private readonly IConfigurationRoot _configuration;
        private readonly ILogger _logger;
        private readonly GlobalExceptionHandler _exceptionHandler;

        public InteractionHandler(DiscordShardedClient client, InteractionService handler, IServiceProvider services, IConfigurationRoot config, GlobalExceptionHandler exceptionHandler)
        {
            _client = client;
            _handler = handler;
            _services = services;
            _configuration = config;
            _logger = services.GetRequiredService<ILogger<InteractionHandler>>();
            _exceptionHandler = exceptionHandler;
        }

        public async Task InitializeAsync()
        {
            // Process when the client is ready, so we can register our commands.
            _client.ShardReady += ReadyAsync;

            // Add the public modules that inherit InteractionModuleBase<T> to the InteractionService
            var moduleResult = await _handler.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
            var modules = moduleResult.ToList();
            _logger.LogInformation("Registered {Count} interaction modules: {Modules}",
                modules.Count,
                string.Join(", ", modules.Select(m => m.Name)));

            // Hook up global error handling for command execution
            _handler.InteractionExecuted += HandleInteractionExecutedAsync;

            // Process the InteractionCreated payloads to execute Interactions commands
            _client.InteractionCreated += HandleInteraction;
        }

        private async Task ReadyAsync(DiscordSocketClient arg)
        {
             // Context & Slash commands can be automatically registered, but this process needs to happen after the client enters the READY state.
            // Since Global Commands take around 1 hour to register, we should use a test guild to instantly update and test our commands.
            if (NinjaBot.IsDebug())
            {
                _logger.LogInformation("Registering {Count} slash commands to test guild {GuildId}",
                    _handler.SlashCommands.Count,
                    _configuration["testGuild"]);
                await _handler.RegisterCommandsToGuildAsync(Convert.ToUInt64(_configuration["testGuild"]), true);
                _logger.LogInformation("Successfully registered commands to test guild");
            }
            else
            {
                _logger.LogInformation("Registering {Count} slash commands globally", _handler.SlashCommands.Count);
                await _handler.RegisterCommandsGloballyAsync(true);
                _logger.LogInformation("Successfully registered commands globally");
            }
        }

        private async Task HandleInteraction(SocketInteraction interaction)
        {
            try
            {
                _logger.LogInformation("Received interaction: Type={Type}, User={User}, Data={Data}",
                    interaction.Type,
                    interaction.User.Username,
                    interaction is SocketSlashCommand cmd ? cmd.Data.Name : interaction.GetType().Name);

                // Skip modal interactions that are handled by event-based handlers (UserInteraction.HandleModal)
                // These modals use custom IDs like "joining_message", "parting_message", "discord_server_note"
                // and are processed via the ModalSubmitted event
                if (interaction is SocketModal modal)
                {
                    var customId = modal.Data.CustomId;
                    if (customId == "joining_message" || customId == "parting_message" || customId == "discord_server_note")
                    {
                        // This modal is handled by the ModalSubmitted event handler in UserInteraction
                        // Don't process it here to avoid "Cannot respond twice" error
                        _logger.LogInformation("Skipping modal {CustomId} - handled by UserInteraction", customId);
                        return;
                    }
                }

                // Pattern #3: Scope-at-Boundary is handled automatically by Discord.NET's AutoServiceScopes
                // The InteractionService creates a new scope for each interaction automatically
                // We pass the root service provider, and Discord.NET handles scope creation/disposal
                var context = new ShardedInteractionContext(_client, interaction);

                _logger.LogInformation("Executing command with AutoServiceScopes");
                // Execute the incoming command - Discord.NET will create and manage the scope
                await _handler.ExecuteCommandAsync(context, _services);
                _logger.LogInformation("Command execution completed");

                // Note: Error handling is now done in HandleInteractionExecutedAsync
            }
            catch (Exception ex)
            {
                // Log unexpected errors that occur before command execution
                _logger.LogError(ex, "Unexpected error in HandleInteraction before command execution");

                // Try to respond with an error message
                try
                {
                    if (interaction.Type is InteractionType.ApplicationCommand)
                    {
                        if (!interaction.HasResponded)
                        {
                            await interaction.RespondAsync(
                                "❌ An unexpected error occurred. Please try again later.",
                                ephemeral: true);
                        }
                        else
                        {
                            await interaction.FollowupAsync(
                                "❌ An unexpected error occurred. Please try again later.",
                                ephemeral: true);
                        }
                    }
                }
                catch (Exception responseEx)
                {
                    _logger.LogError(responseEx, "Failed to send error response");
                }
            }
        }

        /// <summary>
        /// Handles the result of command execution, including errors and exceptions
        /// </summary>
        private async Task HandleInteractionExecutedAsync(
            ICommandInfo commandInfo,
            IInteractionContext context,
            IResult result)
        {
            // Handle command execution exceptions
            if (result.Error == InteractionCommandError.Exception && result is ExecuteResult executeResult)
            {
                await _exceptionHandler.HandleInteractionExceptionAsync(
                    context,
                    result,
                    executeResult.Exception);
                return;
            }

            // Handle other command failures (permissions, validation, etc.)
            if (!result.IsSuccess)
            {
                await _exceptionHandler.HandleCommandResultAsync(context, result);
            }
        }
    }
}