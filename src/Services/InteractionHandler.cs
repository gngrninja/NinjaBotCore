using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
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
        private readonly StartupService _startupService;

        public InteractionHandler(DiscordShardedClient client, InteractionService handler, IServiceProvider services, IConfigurationRoot config, GlobalExceptionHandler exceptionHandler, StartupService startupService)
        {
            _client = client;
            _handler = handler;
            _services = services;
            _configuration = config;
            _logger = services.GetRequiredService<ILogger<InteractionHandler>>();
            _exceptionHandler = exceptionHandler;
            _startupService = startupService;
        }

        public async Task InitializeAsync()
        {
            // Add the public modules that inherit InteractionModuleBase<T> to the InteractionService
            var moduleResult = await _handler.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
            var modules = moduleResult.ToList();
            _logger.LogInformation("Registered {Count} interaction modules: {Modules}",
                modules.Count,
                string.Join(", ", modules.Select(m => m.Name)));

            // Log registered modal handlers for debugging
            var modalCommands = _handler.ModalCommands;
            _logger.LogInformation("Registered {Count} modal handlers: {Modals}",
                modalCommands.Count,
                string.Join(", ", modalCommands.Select(m => m.Name)));

            // Log registered component handlers for debugging
            var componentCommands = _handler.ComponentCommands;
            _logger.LogInformation("Registered {Count} component handlers: {Components}",
                componentCommands.Count,
                string.Join(", ", componentCommands.Select(c => c.Name)));

            // Hook up InteractionService internal logging
            _handler.Log += LogAsync;

            // Hook up global error handling for command execution
            _handler.InteractionExecuted += HandleInteractionExecutedAsync;

            // Process the InteractionCreated payloads to execute Interactions commands
            _client.InteractionCreated += HandleInteraction;

            // Wait for all shards to be ready before registering commands
            // This prevents multiple registrations when using multiple shards
            try
            {
                _logger.LogInformation("Waiting for all shards to be ready before registering commands...");
                await _startupService.AllShardsReady;

                _logger.LogInformation("All shards ready. Registering {Count} slash commands...",
                    _handler.SlashCommands.Count);
                await RegisterCommandsAsync();

                _logger.LogInformation("Command registration completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register slash commands. Bot will continue running, but commands may not be available. " +
                    "Common causes: Missing 'applications.commands' scope in OAuth2 URL, or bot was kicked and re-invited without proper permissions.");
                // Don't throw - allow bot to continue running even if command registration fails
            }
        }

        private async Task RegisterCommandsAsync()
        {
            // Context & Slash commands can be automatically registered, but this process needs to happen after the client enters the READY state.
            // Since Global Commands take around 1 hour to register, we should use a test guild to instantly update and test our commands.
            // This method is called once after all shards are ready to avoid duplicate registrations.
            if (NinjaBot.IsDebug())
            {
                _logger.LogInformation("All shards ready - Registering {Count} slash commands to test guild {GuildId}",
                    _handler.SlashCommands.Count,
                    _configuration["testGuild"]);
                await _handler.RegisterCommandsToGuildAsync(Convert.ToUInt64(_configuration["testGuild"]), true);
                _logger.LogInformation("Successfully registered commands to test guild");
            }
            else
            {
                _logger.LogInformation("All shards ready - Registering {Count} slash commands globally", _handler.SlashCommands.Count);
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

                // Log modal/component routing for debugging
                if (interaction is SocketModal modal)
                {
                    _logger.LogDebug("Modal {CustomId} routing to InteractionService", modal.Data.CustomId);
                }

                if (interaction is SocketMessageComponent component)
                {
                    var customId = component.Data?.CustomId;
                    if (!string.IsNullOrEmpty(customId))
                    {
                        _logger.LogDebug("Component {CustomId} routing to InteractionService", customId);
                    }
                    else
                    {
                        _logger.LogWarning("Component has null or empty CustomId");
                    }
                }

                // Pattern #3: Scope-at-Boundary is handled automatically by Discord.NET's AutoServiceScopes
                // The InteractionService creates a new scope for each interaction automatically
                // We pass the root service provider, and Discord.NET handles scope creation/disposal
                var context = new ShardedInteractionContext(_client, interaction);

                // Execute the incoming command - Discord.NET will create and manage the scope
                await _handler.ExecuteCommandAsync(context, _services);

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
            // Log UnknownCommand errors for debugging - all modals/components should have attribute handlers
            if (result.Error == InteractionCommandError.UnknownCommand)
            {
                if (context.Interaction is SocketMessageComponent msgComponent)
                {
                    _logger.LogWarning("UnknownCommand for component {CustomId} - handler may not be registered", msgComponent.Data?.CustomId);
                }
                else if (context.Interaction is SocketModal modal)
                {
                    _logger.LogWarning("UnknownCommand for modal {CustomId} - handler may not be registered", modal.Data.CustomId);
                }
            }

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

        /// <summary>
        /// Handles internal logging from InteractionService
        /// </summary>
        private Task LogAsync(LogMessage message)
        {
            var logLevel = message.Severity switch
            {
                LogSeverity.Critical => LogLevel.Critical,
                LogSeverity.Error => LogLevel.Error,
                LogSeverity.Warning => LogLevel.Warning,
                LogSeverity.Info => LogLevel.Information,
                LogSeverity.Verbose => LogLevel.Debug,
                LogSeverity.Debug => LogLevel.Debug,
                _ => LogLevel.Information
            };

            _logger.Log(logLevel, message.Exception,
                "[InteractionService] {Source}: {Message}",
                message.Source, message.Message);

            return Task.CompletedTask;
        }
    }
}