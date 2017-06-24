using NinjaBotCore.Database;
using NinjaBotCore.Models.OxfordDictionary;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Fun
{
    public class FunCommands : ModuleBase<SocketCommandContext>
    {
        private static ChannelCheck _cc = null;
        private static OxfordApi _oxApi = null;        
        private DiscordSocketClient _client;
        private CommandService _commands;

        public FunCommands(DiscordSocketClient client, CommandService commands, ChannelCheck cc, OxfordApi oxApi)
        {
            try
            {
                if (_cc == null)
                {
                    _cc = cc;
                }
                if (_oxApi == null)
                {
                    _oxApi = oxApi;
                }
                if (_commands == null)
                {
                    _commands = commands;
                }
                if (_client == null)
                {
                    _client = client;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Something went wrong creating the fun class: {ex.Message}");
            }        
        }

        [Command("Set-Status", RunMode = RunMode.Async)]
        [RequireOwner]
        public async Task SetStatus([Remainder] string args = null)
        {                        
            await _client.SetGameAsync(args);
        }

        [Command("donate", RunMode = RunMode.Async)]        
        [Summary("Help keep ninjaBot going!")]
        public async Task Donate()
        {
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"Would you like to help keep NinjaBot going?");
            sb.AppendLine();
            sb.AppendLine($"Every little bit counts!");
            sb.AppendLine();
            sb.AppendLine($"[Donate To Support NinjaBot!]({NinjaBot.DonateUrl}/5) :thumbsup:");

            embed.ThumbnailUrl = "https://static1.squarespace.com/static/5644323de4b07810c0b6db7b/t/5931c57f46c3c47b464d717a/1496434047310/FdxsNNRt.jpg";            
            embed.WithColor(new Color(0, 255, 0));
            embed.Title = $"{Context.User.Username}, help keep NinjaBot going!";            
            embed.Description = sb.ToString();

            await _cc.Reply(Context, embed);
        }

        [Command("define", RunMode = RunMode.Async)]
        [Summary("Get the definition of a word")]
        public async Task DefineWord([Remainder] string args)
        {
            StringBuilder sb = new StringBuilder();
            var embed = new EmbedBuilder();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();
            var result = _oxApi.searchOxford(args);
            OxfordResponses.OxfordDefinition definition = null;
            int limit = 2;
            if (result.metadata.total > 0)
            {
                definition = _oxApi.defineOxford(result.results[0].id);
            }
            if (definition != null)
            {
                Console.WriteLine(definition.results[0].lexicalEntries.Count());
                embed.Title = $"Definition for: **{definition.results[0].id}**";
                for (int i = 0; i <= definition.results[0].lexicalEntries.Count() - 1 && i < limit; i++)
                {
                    var entries = definition.results[0].lexicalEntries[i].entries.FirstOrDefault();
                    if (entries.senses != null)
                    {
                        var senses = definition.results[0].lexicalEntries[i].entries[0].senses.FirstOrDefault();
                        sb.AppendLine($"**{definition.results[0].lexicalEntries[i].lexicalCategory}**");
                        if (senses.definitions != null)
                        {
                            sb.AppendLine($"{senses.definitions[0]}\n");
                        }
                        else if (senses.crossReferenceMarkers != null)
                        {
                            sb.AppendLine($"{senses.crossReferenceMarkers[0]}");
                        }
                        else
                        {
                            sb.AppendLine($"No definition found :(\n");
                        }
                    }
                    else
                    {
                        sb.AppendLine($"{definition.results[0].lexicalEntries[0].derivativeOf[0].text}");
                        break;
                    }
                }
                var lexicalEntries = definition.results[0].lexicalEntries.FirstOrDefault();
                if (lexicalEntries.pronunciations != null)
                {
                    sb.AppendLine($"[Pronunciation]({lexicalEntries.pronunciations[0].audioFile})");
                }
                //await ReplyAsync(, isTTS: true);
            }
            else
            {
                embed.Title = $"Definition for: **{args}**";
                sb.AppendLine($"No definition found :(");
            }

            embed.Description = sb.ToString();
            await _cc.Reply(Context, embed);
        }

        [Command("8-ball", RunMode = RunMode.Async)]
        [Alias("8ball", "ask")]
        [Summary("Asks the Ninja 8-Ball a Question")]
        public async Task AskQuestion([Remainder] string args)
        {
            var embed = new EmbedBuilder();
            Random r = new Random();
            var answers = new List<C8Ball>();
            using (var db = new NinjaBotEntities())
            {
                answers = db.C8Ball.ToList();
                if (answers == null)
                {
                    answers.Add(new C8Ball
                    {
                        AnswerId = 0,
                        Answer = "No! (cant access DB)",
                        Color = "Red"
                    });
                }
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
            await _cc.Reply(Context, embed);
        }

        [Command("ping", RunMode = RunMode.Async)]
        private async Task GetPing()
        {
            string pingTime = string.Empty;
            pingTime = $"Ping time for bot is **{_client.Latency}**ms";
            await _cc.Reply(Context, pingTime);            
        }

        [Command("help", RunMode = RunMode.Async)]
        [Alias("halp")]
        [Summary("Does this, gets help")]
        public async Task Help()
        {           
            var embed = new EmbedBuilder();
            StringBuilder sb = new StringBuilder();
            embed.Title = $"NinjaBot Help!";
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();
            embed.WithColor(new Color(0, 0, 255));
            sb.AppendLine("Here are a few commands to try:");
            sb.AppendLine($"\t :black_small_square: WoW Commands: {NinjaBot.Prefix}wow **|** {NinjaBot.Prefix}armory characterName **|** {NinjaBot.Prefix}logs **|** {NinjaBot.Prefix}set-guild **|** {NinjaBot.Prefix}get-guild");
            sb.AppendLine($"\t :black_small_square: Rocket League Stats: {NinjaBot.Prefix}rlstats");            
            sb.AppendLine($"\t :black_small_square: Server Note Commands: {NinjaBot.Prefix}note **|** {NinjaBot.Prefix}set-note note goes here");
            sb.AppendLine($"\t :black_small_square: Away System Commands: {NinjaBot.Prefix}away (reason) **|** {NinjaBot.Prefix}back");
            sb.AppendLine($"\t :black_small_square: Search YouTube: {NinjaBot.Prefix}ysearch search term");
            sb.AppendLine($"\t :black_small_square: Define a Word: {NinjaBot.Prefix}define word");
            sb.AppendLine($"\t :black_small_square: Ask Ninja 8-ball a question: {NinjaBot.Prefix}8ball question");
            sb.AppendLine($"\t :black_small_square: Greeting Commands: {NinjaBot.Prefix}toggle-greetings **|** {NinjaBot.Prefix}set-join-message User join greeting **|** {NinjaBot.Prefix}set-part-message User left message");
            sb.AppendLine();
            sb.AppendLine($"**For a more detailed command list, please visit:** http://gngr.ninja/bot");
            embed.Description = sb.ToString();
            await _cc.Reply(Context, embed);

            //var pages = new List<String>();
            //var commands = _commands.Commands.ToList();
            //int commandCount = commands.Count();
            //StringBuilder pageStrings = new StringBuilder();
            //string oneOffs = string.Empty;
            //for (int i = 0; i < commandCount; i++)
            //{
            //    pageStrings.Clear();
            //    int difference = commandCount - i;
            //    if (difference >= 10)
            //    {
            //        int pagesAdded = 0;
            //        do
            //        {
            //            string name = commands[i].Name;
            //            string summary = commands[i].Summary;
            //            if (string.IsNullOrEmpty(summary))
            //            {
            //                summary = "No summary available!";
            //            }
            //            pageStrings.AppendLine($"**{name}** (*{summary}*)");
            //            i++;
            //            pagesAdded++;
            //        }
            //        while (pagesAdded < 10);
            //        i--;
            //        pages.Add(pageStrings.ToString());
            //    }
            //    else
            //    {
            //        string name = commands[i].Name;
            //        string summary = commands[i].Summary;
            //        if (string.IsNullOrEmpty(summary))
            //        {
            //            summary = "No summary available!";
            //        }
            //        oneOffs += $"**{name}** (*{summary}*)\n";
            //    }
            //}
            //pages.Add(oneOffs);            
            //try
            //{
            //    var newList = commands.ChunkBy(10);
            //    Console.WriteLine(newList.Count);
            //    var message = new PaginatedMessage(pages, $"NinjaBot Command List ({commandCount} total commands)", new Color(0, 255, 0), Context.User);
            //    await _paginator.SendPaginatedMessageAsync(Context.Channel as IMessageChannel, message);
            //}
            //catch (Exception ex)
            //{
            //    await _cc.Reply(Context, $"Unable to get you help, sorry! [{ex.Message}]");
            //}
        }
    }
}