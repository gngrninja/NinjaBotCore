using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Services
{
    public class LoggingService
    {
        private readonly ILogger _logger;
        
        public LoggingService(ILogger logger)
        {            
            _logger   = logger;
        }
        
        public Task OnLogAsync(LogMessage msg)
        {            
            string logText = $"{msg.Source}: {msg.Exception?.ToString() ?? msg.Message}";

            switch (msg.Severity.ToString())
            {
                case "Critical":
                {
                    _logger.LogCritical(logText);
                    break;
                }
                case "Warning":
                {
                    _logger.LogWarning(logText);
                    break;
                }
                case "Info":
                {
                    _logger.LogInformation(logText);
                    break;
                }
                case "Verbose":
                {
                    _logger.LogInformation(logText);
                    break;
                }    
                case "Debug":
                {
                    _logger.LogDebug(logText);
                    break;
                }                                                            
            }
            return Task.CompletedTask;  
        }
    }
}