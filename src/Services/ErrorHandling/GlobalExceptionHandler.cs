using System;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Services.ErrorHandling
{
    public class GlobalExceptionHandler
    {
        private readonly ILogger<GlobalExceptionHandler> _logger;

        public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Handles unhandled exceptions from interaction command execution
        /// </summary>
        public async Task HandleInteractionExceptionAsync(
            IInteractionContext context,
            IResult result,
            Exception exception)
        {
            // Get command name for logging
            var commandName = context.Interaction.Data is IApplicationCommandInteractionData commandData
                ? commandData.Name
                : "Unknown";

            // Log the exception with context
            _logger.LogError(exception,
                "Unhandled exception in command '{Command}' executed by '{User}' ({UserId}) in guild '{Guild}' ({GuildId})",
                commandName,
                context.User.Username,
                context.User.Id,
                context.Guild?.Name ?? "DM",
                context.Guild?.Id ?? 0);

            // Try to respond to the user with a friendly error message
            try
            {
                var embedBuilder = new EmbedBuilder()
                    .WithTitle("❌ Command Error")
                    .WithDescription("An error occurred while processing your command. The issue has been logged and will be investigated.")
                    .WithColor(Color.Red)
                    .WithCurrentTimestamp();

                // Add error details for debugging (only in development or for bot owners)
                if (!string.IsNullOrEmpty(exception.Message))
                {
                    embedBuilder.AddField("Error Type", exception.GetType().Name, inline: true);
                }

                // Determine if we should respond or follow up
                if (context.Interaction.HasResponded)
                {
                    // Already responded, send a follow-up
                    await context.Interaction.FollowupAsync(
                        embed: embedBuilder.Build(),
                        ephemeral: true);
                }
                else
                {
                    // Haven't responded yet, send initial response
                    await context.Interaction.RespondAsync(
                        embed: embedBuilder.Build(),
                        ephemeral: true);
                }
            }
            catch (Exception responseEx)
            {
                // If we can't even send an error message, log it
                _logger.LogError(responseEx,
                    "Failed to send error response to user for command '{Command}'",
                    commandName);
            }
        }

        /// <summary>
        /// Handles command execution results (for non-exception errors like permission failures)
        /// </summary>
        public async Task HandleCommandResultAsync(
            IInteractionContext context,
            IResult result)
        {
            if (result.IsSuccess)
                return;

            // Log the failure
            _logger.LogWarning(
                "Command '{Command}' failed with error: {Error}. Reason: {Reason}",
                context.Interaction.Data is IApplicationCommandInteractionData cmdData ? cmdData.Name : "Unknown",
                result.Error,
                result.ErrorReason);

            // Handle specific error types
            string errorMessage = result.Error switch
            {
                InteractionCommandError.UnmetPrecondition => $"❌ You don't have permission to use this command.\n\n{result.ErrorReason}",
                InteractionCommandError.BadArgs => $"❌ Invalid command arguments.\n\n{result.ErrorReason}",
                InteractionCommandError.ParseFailed => $"❌ Failed to parse command arguments.\n\n{result.ErrorReason}",
                InteractionCommandError.ConvertFailed => $"❌ Failed to convert command arguments.\n\n{result.ErrorReason}",
                InteractionCommandError.Unsuccessful => $"❌ Command execution failed.\n\n{result.ErrorReason}",
                InteractionCommandError.UnknownCommand => "❌ Unknown command.",
                _ => $"❌ An error occurred: {result.ErrorReason}"
            };

            try
            {
                if (context.Interaction.HasResponded)
                {
                    await context.Interaction.FollowupAsync(
                        text: errorMessage,
                        ephemeral: true);
                }
                else
                {
                    await context.Interaction.RespondAsync(
                        text: errorMessage,
                        ephemeral: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send error message to user for command result");
            }
        }
    }
}
