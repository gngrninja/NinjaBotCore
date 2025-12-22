using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Common
{
    public class SanitizingLogger<T> : ILogger<T>
    {
        private readonly ILogger<T> _innerLogger;
        private static readonly Regex[] SensitivePatterns = new[]
        {
            new Regex(@"access_key=[^&\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"api[_-]?key=[^&\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"client[_-]?secret=[^&\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"access[_-]?token=[^&\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };

        public SanitizingLogger(ILogger<T> innerLogger)
        {
            _innerLogger = innerLogger ?? throw new ArgumentNullException(nameof(innerLogger));
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return _innerLogger.BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return _innerLogger.IsEnabled(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Sanitize the formatted message
            var originalMessage = formatter(state, exception);
            var sanitizedMessage = SanitizeMessage(originalMessage);

            // Create a new formatter that returns the sanitized message
            string SanitizedFormatter(TState s, Exception e) => sanitizedMessage;

            _innerLogger.Log(logLevel, eventId, state, exception, SanitizedFormatter);
        }

        private static string SanitizeMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message;
            }

            var sanitized = message;
            foreach (var pattern in SensitivePatterns)
            {
                sanitized = pattern.Replace(sanitized, match =>
                {
                    var key = match.Value.Split('=')[0];
                    return $"{key}=[REDACTED]";
                });
            }

            return sanitized;
        }

        public static string SanitizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return url;
            }

            try
            {
                var uri = new Uri(url);
                var sanitized = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";

                if (!string.IsNullOrEmpty(uri.Query))
                {
                    var sanitizedQuery = SanitizeMessage(uri.Query);
                    sanitized += sanitizedQuery;
                }

                return sanitized;
            }
            catch
            {
                return SanitizeMessage(url);
            }
        }
    }
}
