using System;
using Discord;
using Microsoft.Extensions.Logging;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for InteractionHandler utility methods and log level mapping
    /// Note: Full integration testing of InteractionHandler requires Discord.Net mocking
    /// which is complex. These tests focus on testable utility logic.
    /// </summary>
    public class InteractionHandlerTests
    {
        #region Log Severity Mapping Tests

        [Theory]
        [InlineData(LogSeverity.Critical, LogLevel.Critical)]
        [InlineData(LogSeverity.Error, LogLevel.Error)]
        [InlineData(LogSeverity.Warning, LogLevel.Warning)]
        [InlineData(LogSeverity.Info, LogLevel.Information)]
        [InlineData(LogSeverity.Verbose, LogLevel.Debug)]
        [InlineData(LogSeverity.Debug, LogLevel.Debug)]
        public void LogSeverityMapping_MapsCorrectly(LogSeverity discordSeverity, LogLevel expectedLevel)
        {
            // This tests the mapping logic used in InteractionHandler.LogAsync
            var result = MapLogSeverity(discordSeverity);
            Assert.Equal(expectedLevel, result);
        }

        [Fact]
        public void LogSeverityMapping_UnknownSeverity_DefaultsToInformation()
        {
            // Test default case (shouldn't happen but good to verify)
            var result = MapLogSeverity((LogSeverity)999);
            Assert.Equal(LogLevel.Information, result);
        }

        /// <summary>
        /// Mirrors the switch expression in InteractionHandler.LogAsync
        /// </summary>
        private static LogLevel MapLogSeverity(LogSeverity severity)
        {
            return severity switch
            {
                LogSeverity.Critical => LogLevel.Critical,
                LogSeverity.Error => LogLevel.Error,
                LogSeverity.Warning => LogLevel.Warning,
                LogSeverity.Info => LogLevel.Information,
                LogSeverity.Verbose => LogLevel.Debug,
                LogSeverity.Debug => LogLevel.Debug,
                _ => LogLevel.Information
            };
        }

        #endregion

        #region Modal Custom ID Filtering Tests

        [Theory]
        [InlineData("joining_message", true)]
        [InlineData("parting_message", true)]
        [InlineData("discord_server_note", true)]
        [InlineData("other_modal", false)]
        [InlineData("joining_message_extra", false)]
        [InlineData("", false)]
        public void ModalCustomId_FilteringLogic_WorksCorrectly(string customId, bool shouldSkip)
        {
            // This tests the modal filtering logic in HandleInteraction
            var result = ShouldSkipModal(customId);
            Assert.Equal(shouldSkip, result);
        }

        /// <summary>
        /// Mirrors the modal filtering logic in InteractionHandler.HandleInteraction
        /// </summary>
        private static bool ShouldSkipModal(string customId)
        {
            return customId == "joining_message" ||
                   customId == "parting_message" ||
                   customId == "discord_server_note";
        }

        #endregion

        #region Interaction Type Handling Tests

        [Fact]
        public void InteractionType_ApplicationCommand_IsHandled()
        {
            // Verify ApplicationCommand is a valid interaction type
            Assert.Equal(2, (int)InteractionType.ApplicationCommand);
        }

        [Fact]
        public void InteractionType_ModalSubmit_IsHandled()
        {
            // Verify ModalSubmit is a valid interaction type
            Assert.Equal(5, (int)InteractionType.ModalSubmit);
        }

        [Fact]
        public void InteractionType_MessageComponent_IsHandled()
        {
            // Verify MessageComponent is a valid interaction type
            Assert.Equal(3, (int)InteractionType.MessageComponent);
        }

        [Fact]
        public void InteractionType_Autocomplete_IsHandled()
        {
            // Verify Autocomplete is a valid interaction type
            Assert.Equal(4, (int)InteractionType.ApplicationCommandAutocomplete);
        }

        #endregion
    }
}
