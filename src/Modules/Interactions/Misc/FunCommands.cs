using NinjaBotCore.Database;
using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Services;
using Discord.Interactions;

namespace NinjaBotCore.Modules.Interactions.Fun
{
    public class FunCommands : NinjaBotBaseModule
    {
        private DiscordShardedClient _client;
        private readonly IConfigurationRoot _config;

        public FunCommands(DiscordShardedClient client, IConfigurationRoot config, IServiceScopeFactory scopeFactory)
            : base(scopeFactory)
        {
            _client = client;
            _config = config;
        }

        [SlashCommand("setstatus", "set status of the bot")]
        [RequireOwner]
        public async Task SetStatus(string args = null)
        {                        
            await _client.SetGameAsync(args);
            await RespondAsync(text: $"Status set to {args}", ephemeral: true);
        }

        [SlashCommand("donate", "donate!")]        
        public async Task Donate()
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"Would you like to help keep NinjaBot going?");
            sb.AppendLine();
            sb.AppendLine($"Every little bit counts!");
            sb.AppendLine();
            sb.AppendLine($"[Donate To Support NinjaBot!]({_config["DonateUrl"]}/5) :thumbsup:");

            embed.ThumbnailUrl = "https://static1.squarespace.com/static/5644323de4b07810c0b6db7b/t/5931c57f46c3c47b464d717a/1496434047310/FdxsNNRt.jpg";            
            embed.WithColor(new Color(0, 255, 0));
            embed.Title = $"{Context.User.Username}, help keep NinjaBot going!";            
            embed.Description = sb.ToString();

            await RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        [SlashCommand("8ball", "Ask Ninja 8-ball a question!")]
        public async Task AskQuestion(string args)
        {
            var embed = new EmbedBuilder();
            Random r = new Random();

            var answers = await WithDbAsync(db => db.C8Ball.ToListAsync());
            if (answers == null || !answers.Any())
            {
                answers = new List<C8Ball>
                {
                    new C8Ball
                    {
                        AnswerId = 0,
                        Answer = "No! (cant access DB)",
                        Color = "Red"
                    }
                };
            }
            var answer = answers[r.Next(answers.Count())];
            string answerText = string.Empty;
            if (answer != null)
            {
                answerText = answer.Answer;
                switch (answer.Color.ToLower())
                {
                    case "yellow":
                        {
                            embed.WithColor(new Color(255, 255, 0));
                            break;
                        }
                    case "red":
                        {
                            embed.WithColor(new Color(255, 0, 0));
                            break;
                        }
                    case "green":
                        {
                            embed.WithColor(new Color(0, 128, 0));
                            break;
                        }
                }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("**   **");
                sb.AppendLine($"**{answerText}**");
                sb.AppendLine("**   **\n");
                embed.Description = sb.ToString();
            }
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();
            embed.Title = $":crystal_ball: Ninja 8-Ball: (**{args}**) :crystal_ball:";
            await RespondAsync(embed: embed.Build());
        }

        [SlashCommand("ping", "get ping time")]
        private async Task GetPing()
        {
            string pingTime = string.Empty;
            pingTime = $"Ping time for bot is **{_client.Latency}**ms";
            await RespondAsync(pingTime);
        }
    }
}