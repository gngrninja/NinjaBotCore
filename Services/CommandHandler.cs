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

namespace NinjaBotCore.Services
{
    public class CommandHandler
    {
        private CommandService _commands;
        private DiscordSocketClient _client;
        private readonly IServiceProvider _provider;
        private readonly IConfigurationRoot _config;

        public CommandHandler(IServiceProvider provider, IConfigurationRoot config)
        {
            _config = config;
            _provider = provider;
            _client = _provider.GetService<DiscordSocketClient>();
            _commands = _provider.GetService<CommandService>();
            _client.MessageReceived += HandleCommand;            
        }

        public async Task HandleCommand(SocketMessage parameterMessage)
        {
            // Don't handle the command if it is a system message
            var message = parameterMessage as SocketUserMessage;
            if (message == null) return;

            // Mark where the prefix ends and the command begins
            int argPos = 0;
            // Determine if the message has a valid prefix, adjust argPos 
            if (!(message.HasMentionPrefix(_client.CurrentUser, ref argPos) || message.HasCharPrefix(Char.Parse(_config["prefix"]), ref argPos))) return;

            // Create a Command Context
            var context = new SocketCommandContext(_client, message);

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
            var result = await _commands.ExecuteAsync(context, argPos, _provider);

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

        private static async Task LogCommandUsage(SocketCommandContext context, IResult result)
        {
            var request = new Request();
            if (context.Channel is IGuildChannel)
            {
                request.ServerID = (long)context.Guild.Id;
                request.ServerName = context.Guild.Name;
                System.Console.WriteLine($"+[{System.DateTime.Now.ToString("t")}] User: {context.User.Username} Guild: {context.Guild.Name} -> {context.Message.Content}");
            }
            else
            {
                System.Console.WriteLine($"+[{System.DateTime.Now.ToString("t")}] User: {context.User.Username} -> {context.Message.Content}");
            }
            request.ChannelId = (long)context.Channel.Id;
            request.ChannelName = context.Channel.Name;
            request.UserId = (long)context.User.Id;
            request.Command = context.Message.Content;
            request.UserName = context.User.Username;
            request.Success = true;
            request.RequestTime = DateTime.Now;

            string commandIssued = string.Empty;
            if (!result.IsSuccess)
            {
                request.Success = false;
                request.FailureReason = result.ErrorReason;
            }
            using (var db = new NinjaBotEntities())
            {
                db.Requests.Add(request);
                await db.SaveChangesAsync();
            }
        }
    }
}