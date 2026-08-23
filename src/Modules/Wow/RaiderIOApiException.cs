using System;
using System.Net;
using System.Net.Http;

namespace NinjaBotCore.Modules.Wow
{
    public class RaiderIOApiException : HttpRequestException
    {
        public RaiderIOApiException(
            string message,
            HttpStatusCode? statusCode = null,
            Exception innerException = null)
            : base(message, innerException, statusCode)
        {
        }
    }

    public sealed class RaiderIONotFoundException : RaiderIOApiException
    {
        public RaiderIONotFoundException(string message)
            : base(message, HttpStatusCode.NotFound)
        {
        }
    }

    public sealed class RaiderIORateLimitException : RaiderIOApiException
    {
        public RaiderIORateLimitException(string message, TimeSpan retryAfter)
            : base(message, HttpStatusCode.TooManyRequests)
        {
            RetryAfter = retryAfter;
        }

        public TimeSpan RetryAfter { get; }
    }
}
