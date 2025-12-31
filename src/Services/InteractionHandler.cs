using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Services.ErrorHandling;
using System;
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
            await _handler.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

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
                System.Console.WriteLine("debug");
                await _handler.RegisterCommandsToGuildAsync(Convert.ToUInt64(_configuration["testGuild"]), true);
            }  
            else
            {
                await _handler.RegisterCommandsGloballyAsync(true);
            }                
        }

        private async Task HandleInteraction(SocketInteraction interaction)
        {
            try
            {
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
                        return;
                    }
                }

                // Create an execution context that matches the generic type parameter of your InteractionModuleBase<T> modules.
                var context = new ShardedInteractionContext(_client, interaction);

                // Execute the incoming command.
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