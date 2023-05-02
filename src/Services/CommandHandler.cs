using System.Threading.Tasks;
using System.Reflection;
using Discord.Net;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Collections.Generic;
using NinjaBotCore.Database;
using Discord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Services
{
    public class CommandHandler
    {
        private CommandService _commands;
        private DiscordShardedClient _client;
        private readonly IConfigurationRoot _config;
        private readonly ILogger _logger;
        private readonly IServiceProvider _services;

        public CommandHandler(IServiceProvider services)
        {
            _services = services;
            _config = services.GetRequiredService<IConfigurationRoot>();
            _client = services.GetRequiredService<DiscordShardedClient>();
            _commands = services.GetRequiredService<CommandService>();
            _client.MessageReceived += HandleCommand;            
            _logger = services.GetRequiredService<ILogger<CommandHandler>>();
        }

        public async Task HandleCommand(SocketMessage parameterMessage)
        {
            // Don't handle the command if it is a system message            
            var message = parameterMessage as SocketUserMessage;            
            if (message == null) return;

            // Don't listen to bots
            if (message.Source != MessageSource.User) 
            {
                return;
            }
            
            // Mark where the prefix ends and the command begins
            int argPos = 0;
            
            // Create a Command Context
            var context = new ShardedCommandContext(_client, message);

            char prefix = Char.Parse(_config["prefix"]);

            var serverPrefix = GetPrefix((long)context.Guild.Id); 

            if (serverPrefix != null)
            {
                prefix = serverPrefix.Prefix;
            }
            
            // Determine if the message has a valid prefix, adjust argPos
            if (!(message.HasMentionPrefix(_client.CurrentUser, ref argPos) || message.HasCharPrefix(prefix, ref argPos))) return;

           
            //Check blacklist
            List<Blacklist> blacklist = new List<Blacklist>();

            using (var db = new NinjaBotEntities())
            {
                blacklist = db.Blacklist.ToList();
            }
            if (blacklist != null)
            {
                var matched = blacklist.Where(b => b.DiscordUserId == (long)context.User.Id).FirstOrDefault();
                if (matched != null)
                {
                    return;
                }
            }

            // Execute the Command, store the result            
            var result = await _commands.ExecuteAsync(context, argPos, _services);

            await LogCommandUsage(context, result);
            // If the command failed, notify the user
            if (!result.IsSuccess)
            {
                if (result.ErrorReason != "Unknown command.")
                {
                    await message.Channel.SendMessageAsync($"**Error:** {result.ErrorReason}");
                }
            }
        }

        private PrefixList GetPrefix(long serverId)
        {
            PrefixList prefix = null;

            using (var db = new NinjaBotEntities())
            {
                prefix = db.PrefixList.Where(p => p.ServerId == serverId).FirstOrDefault();
            }

            return prefix;
        }

        private async Task LogCommandUsage(SocketCommandContext context, IResult result)
        {
            await Task.Run(async () =>
            {
                if (context.Channel is IGuildChannel)
                {
                    var logTxt = $"User: [{context.User.Username}]<->[{context.User.Id}] Discord Server: [{context.Guild.Name}] -> [{context.Message.Content}]";
                    _logger.LogInformation(logTxt);
                }
                else
                {
                    var logTxt = $"User: [{context.User.Username}]<->[{context.User.Id}] -> [{context.Message.Content}]";
                    _logger.LogInformation(logTxt);
                }
            });
  
            /*
            string commandIssued = string.Empty;
            if (!result.IsSuccess)
            {
                request.Success = false;
                request.FailureReason = result.ErrorReason;
            }
                      request.ChannelId = (long)context.Channel.Id;
            request.ChannelName = context.Channel.Name;
            request.UserId = (long)context.User.Id;
            request.Command = context.Message.Content;
            request.UserName = context.User.Username;
            request.Success = true;
            request.RequestTime = DateTime.Now;
            using (var db = new NinjaBotEntities())
            {
                
                db.Requests.Add(request);
                await db.SaveChangesAsync();
            }
             */
        }
    }
}