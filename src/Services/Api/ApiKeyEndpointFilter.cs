using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Services.Api
{
    public class ApiKeyEndpointFilter : IEndpointFilter
    {
        private readonly string _apiKey;
        private readonly ILogger _logger;

        public ApiKeyEndpointFilter(string apiKey, ILogger logger)
        {
            _apiKey = apiKey;
            _logger = logger;
        }

        public async ValueTask<object?> InvokeAsync(
            EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            if (!string.IsNullOrEmpty(_apiKey))
            {
                var key = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
                if (key != _apiKey)
                {
                    _logger.LogWarning("API request with invalid API key from {IP}",
                        context.HttpContext.Connection.RemoteIpAddress);
                    return Results.Unauthorized();
                }
            }

            return await next(context);
        }
    }
}
