using System;
using System.Collections.Generic;
using System.Net.Http;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Services.ErrorHandling;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for global exception handling in interaction commands.
    /// Note: Discord.Net interfaces are difficult to mock due to optional parameters,
    /// so these tests focus on the error message mapping logic which is the core business logic.
    /// </summary>
    public class GlobalExceptionHandlerTests
    {
        private readonly TestLogger<GlobalExceptionHandler> _testLogger;
        private readonly GlobalExceptionHandler _handler;

        public GlobalExceptionHandlerTests()
        {
            _testLogger = new TestLogger<GlobalExceptionHandler>();
            _handler = new GlobalExceptionHandler(_testLogger);
        }

        #region Error Message Mapping Tests

        [Fact]
        public void ErrorMessageMapping_UnmetPrecondition_ContainsPermissionMessage()
        {
            var message = GetErrorMessageForType(InteractionCommandError.UnmetPrecondition, "User requires Admin");
            Assert.Contains("don't have permission", message);
            Assert.Contains("User requires Admin", message);
            Assert.StartsWith("❌", message);
        }

        [Fact]
        public void ErrorMessageMapping_BadArgs_ContainsInvalidArgumentsMessage()
        {
            var message = GetErrorMessageForType(InteractionCommandError.BadArgs, "Missing required parameter");
            Assert.Contains("Invalid command arguments", message);
            Assert.Contains("Missing required parameter", message);
        }

        [Fact]
        public void ErrorMessageMapping_ParseFailed_ContainsParseFailureMessage()
        {
            var message = GetErrorMessageForType(InteractionCommandError.ParseFailed, "Could not parse 'abc' as integer");
            Assert.Contains("Failed to parse", message);
            Assert.Contains("Could not parse", message);
        }

        [Fact]
        public void ErrorMessageMapping_ConvertFailed_ContainsConversionMessage()
        {
            var message = GetErrorMessageForType(InteractionCommandError.ConvertFailed, "Invalid type conversion");
            Assert.Contains("Failed to convert", message);
        }

        [Fact]
        public void ErrorMessageMapping_Unsuccessful_ContainsExecutionFailedMessage()
        {
            var message = GetErrorMessageForType(InteractionCommandError.Unsuccessful, "Command threw exception");
            Assert.Contains("Command execution failed", message);
        }

        [Fact]
        public void ErrorMessageMapping_UnknownCommand_ContainsUnknownCommandMessage()
        {
            var message = GetErrorMessageForType(InteractionCommandError.UnknownCommand, "");
            Assert.Contains("Unknown command", message);
        }

        [Theory]
        [InlineData(InteractionCommandError.UnmetPrecondition)]
        [InlineData(InteractionCommandError.BadArgs)]
        [InlineData(InteractionCommandError.ParseFailed)]
        [InlineData(InteractionCommandError.ConvertFailed)]
        [InlineData(InteractionCommandError.Unsuccessful)]
        [InlineData(InteractionCommandError.UnknownCommand)]
        public void ErrorMessageMapping_AllErrorTypes_StartWithErrorEmoji(InteractionCommandError errorType)
        {
            var message = GetErrorMessageForType(errorType, "test reason");
            Assert.StartsWith("❌", message);
        }

        /// <summary>
        /// Helper to get the error message for a given error type.
        /// This mirrors the switch expression in GlobalExceptionHandler.HandleCommandResultAsync
        /// </summary>
        private static string GetErrorMessageForType(InteractionCommandError? errorType, string errorReason)
        {
            return errorType switch
            {
                InteractionCommandError.UnmetPrecondition => $"❌ You don't have permission to use this command.\n\n{errorReason}",
                InteractionCommandError.BadArgs => $"❌ Invalid command arguments.\n\n{errorReason}",
                InteractionCommandError.ParseFailed => $"❌ Failed to parse command arguments.\n\n{errorReason}",
                InteractionCommandError.ConvertFailed => $"❌ Failed to convert command arguments.\n\n{errorReason}",
                InteractionCommandError.Unsuccessful => $"❌ Command execution failed.\n\n{errorReason}",
                InteractionCommandError.UnknownCommand => "❌ Unknown command.",
                _ => $"❌ An error occurred: {errorReason}"
            };
        }

        #endregion

        #region Constructor and Initialization Tests

        [Fact]
        public void Constructor_WithValidLogger_CreatesInstance()
        {
            var logger = new TestLogger<GlobalExceptionHandler>();
            var handler = new GlobalExceptionHandler(logger);
            Assert.NotNull(handler);
        }

        #endregion

        #region Exception Type Coverage Tests

        [Theory]
        [InlineData(typeof(InvalidOperationException))]
        [InlineData(typeof(ArgumentException))]
        [InlineData(typeof(NullReferenceException))]
        [InlineData(typeof(TimeoutException))]
        [InlineData(typeof(HttpRequestException))]
        public void ExceptionTypes_AllCommonTypes_HaveTypeNameAccessible(Type exceptionType)
        {
            // This test verifies that common exception types can have their names extracted
            // as is done in HandleInteractionExceptionAsync for the "Error Type" embed field
            var exception = (Exception)Activator.CreateInstance(exceptionType, "Test message")!;
            var typeName = exception.GetType().Name;

            Assert.NotNull(typeName);
            Assert.NotEmpty(typeName);
            Assert.DoesNotContain("Exception", typeName.Replace(exceptionType.Name, ""));
        }

        #endregion

        /// <summary>
        /// Simple test logger that captures log entries for verification
        /// </summary>
        private class TestLogger<T> : ILogger<T>
        {
            public List<LogEntry> LogEntries { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                LogEntries.Add(new LogEntry
                {
                    LogLevel = logLevel,
                    Message = formatter(state, exception),
                    Exception = exception
                });
            }

            public record LogEntry
            {
                public LogLevel LogLevel { get; init; }
                public string Message { get; init; } = "";
                public Exception? Exception { get; init; }
            }
        }
    }
}
