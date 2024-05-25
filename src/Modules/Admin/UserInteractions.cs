using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Microsoft.Extensions.DependencyInjection;
using Discord.Net;
using Discord.Commands;
using Discord.WebSocket;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Modules.Admin
{
    public class UserInteraction
    {
        private readonly ILogger _logger;
        private readonly DiscordShardedClient _client;

        public UserInteraction(IServiceProvider services)
        {
            _logger = services.GetRequiredService<ILogger<UserInteraction>>();
            _client = services.GetRequiredService<DiscordShardedClient>();

            services.GetRequiredService<DiscordShardedClient>().UserJoined += HandleGreeting;
            services.GetRequiredService<DiscordShardedClient>().UserLeft += HandleParting;
            services.GetRequiredService<DiscordShardedClient>().ModalSubmitted += HandleModal;

            _logger.LogInformation($"UserInteractions loaded");
        }

        private async Task HandleModal(SocketModal modal)
        {
            await Task.Run(async () =>
            {
                // Get the values of components.
                List<SocketMessageComponentData> components =
                    modal.Data.Components.ToList();
                var embed = new EmbedBuilder();
                StringBuilder sb = new StringBuilder();
                var guildInfo = _client.GetGuild((ulong)modal.GuildId);
                switch (modal.Data.CustomId)
                {
                    case "joining_message":
                    {
                        await HandleJoiningModal(modal, components, embed, sb, guildInfo);
                        break;
                    }
                    case "parting_message":
                    {
                        await HandlePartingModal(modal, components, embed, sb, guildInfo);
                        break;
                    }
                    case "discord_server_note":
                    {
                        await HandleNoteModal(modal, components, embed, sb, guildInfo);
                        break;
                    }
                }
            });
        }

        private static async Task HandlePartingModal(SocketModal modal, List<SocketMessageComponentData> components, EmbedBuilder embed, StringBuilder sb, SocketGuild guildInfo)
        {
            string partingMessage = components.First(x => x.CustomId == "parting_message").Value;
            using (var db = new NinjaBotEntities())
            {
                if (!string.IsNullOrEmpty(partingMessage))
                {
                    try
                    {
                        embed.Title = $"Parting message change for {guildInfo.Name}";
                        sb.AppendLine("New message:");
                        sb.AppendLine(partingMessage);
                        var guildGreetingInfo = db.ServerGreetings.Where(g => g.DiscordGuildId == (long)modal.GuildId).FirstOrDefault();
                        if (guildGreetingInfo != null)
                        {
                            guildGreetingInfo.PartingMessage = partingMessage.Trim();
                            guildGreetingInfo.SetById = (long)modal.User.Id;
                            guildGreetingInfo.SetByName = modal.User.Username;
                            guildGreetingInfo.TimeSet = DateTime.Now;
                        }
                        else
                        {
                            db.ServerGreetings.Add(new ServerGreeting
                            {
                                DiscordGuildId = (long)modal.GuildId,
                                PartingMessage = partingMessage.Trim(),
                                SetById = (long)modal.User.Id,
                                SetByName = modal.User.Username,
                                TimeSet = DateTime.Now
                            });
                        }
                        await db.SaveChangesAsync();
                    }
                    catch (Exception)
                    {
                        embed.Title = $"Error changing message";
                        sb.AppendLine($"{modal.User.Mention},");
                        sb.AppendLine($"I've encounted an error, please contact the owner for help.");
                    }
                }
            }
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = guildInfo.IconUrl;
            await modal.RespondAsync(text: null, embed: embed.Build(), ephemeral: true);
        }

        private static async Task HandleJoiningModal(SocketModal modal, List<SocketMessageComponentData> components, EmbedBuilder embed, StringBuilder sb, SocketGuild guildInfo)
        {
            string joiningMessage = components.First(x => x.CustomId == "joining_message").Value;
            using (var db = new NinjaBotEntities())
            {
                if (!string.IsNullOrEmpty(joiningMessage))
                {
                    try
                    {
                        embed.Title = $"Joining message change for {guildInfo.Name}";
                        sb.AppendLine("New message:");
                        sb.AppendLine(joiningMessage);
                        var guildGreetingInfo = db.ServerGreetings.Where(g => g.DiscordGuildId == (long)modal.GuildId).FirstOrDefault();
                        if (guildGreetingInfo != null)
                        {
                            guildGreetingInfo.Greeting = joiningMessage.Trim();
                            guildGreetingInfo.SetById = (long)modal.User.Id;
                            guildGreetingInfo.SetByName = modal.User.Username;
                            guildGreetingInfo.TimeSet = DateTime.Now;
                        }
                        else
                        {
                            db.ServerGreetings.Add(new ServerGreeting
                            {
                                DiscordGuildId = (long)modal.GuildId,
                                Greeting = joiningMessage.Trim(),
                                SetById = (long)modal.User.Id,
                                SetByName = modal.User.Username,
                                TimeSet = DateTime.Now
                            });
                        }
                        await db.SaveChangesAsync();
                    }
                    catch (Exception)
                    {
                        embed.Title = $"Error changing message";
                        sb.AppendLine($"{modal.User.Mention},");
                        sb.AppendLine($"I've encounted an error, please contact the owner for help.");
                    }
                }
            }
            embed.Description = sb.ToString();
            embed.WithColor(new Color(0, 255, 0));
            embed.ThumbnailUrl = guildInfo.IconUrl;
            await modal.RespondAsync(text: null, embed: embed.Build(), ephemeral: true);
        }        

        private static async Task HandleNoteModal(SocketModal modal, List<SocketMessageComponentData> components, EmbedBuilder embed, StringBuilder sb, SocketGuild guildInfo)
        {
            string noteText = components.First(x => x.CustomId == "note_text").Value;
            try
            {
                using (var db = new NinjaBotEntities())
                {
                    var currentNote = db.Notes.FirstOrDefault(c => c.ServerId == (long)guildInfo.Id);
                    if (currentNote == null)
                    {
                        Note n = new Note()
                        {
                            Note1 = noteText,
                            ServerId = (long)guildInfo.Id,
                            ServerName = guildInfo.Name,
                            SetBy = modal.User.Username,
                            SetById = (long)modal.User.Id,
                            TimeSet = DateTime.Now
                        };
                        db.Notes.Add(n);
                    }
                    else
                    {
                        currentNote.Note1 = noteText;
                        currentNote.SetBy = modal.User.Username;
                        currentNote.SetById = (long)modal.User.Id;
                        currentNote.TimeSet = DateTime.Now;
                    }
                    await db.SaveChangesAsync();
                }
                sb.AppendLine($"Note successfully added for server [**{guildInfo.Name}**] by [**{modal.User.Username}**]!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting note {ex.Message}");
                sb.AppendLine($"Something went wrong adding a note for server [**{guildInfo.Name}**] :(");
            }
            embed.Title = $":notepad_spiral:Notes for {guildInfo.Name}:notepad_spiral:";
            embed.Description = sb.ToString();
            embed.ThumbnailUrl = guildInfo.IconUrl;
            embed.WithColor(new Color(0, 255, 0));
            await modal.RespondAsync(embed: embed.Build(), ephemeral: true);
        }

        private async Task HandleParting(SocketGuild guild, SocketUser socketUser)
        {
            await Task.Run(async () =>
            {                
                var user = (SocketGuildUser)socketUser;
                ServerGreeting shouldGreet = GetGreeting(user);                                 
                if (shouldGreet != null && shouldGreet.GreetUsers == true)
                {      
                    var sb = new StringBuilder();   
                    ISocketMessageChannel messageChannel = null;                  
                    try
                    {                            
                        if (shouldGreet.GreetingChannelId != 0)
                        {                            
                            if (shouldGreet.PartingChannelId != null)
                            {
                                messageChannel = user.Guild.GetChannel((ulong)shouldGreet.PartingChannelId) as ISocketMessageChannel;
                            }      
                            else
                            {
                                messageChannel = user.Guild.GetChannel((ulong)shouldGreet.GreetingChannelId) as ISocketMessageChannel;
                            }                      
                        }
                        else
                        {
                            messageChannel = user.Guild.DefaultChannel as ISocketMessageChannel;
                        }
                        if (messageChannel != null)
                        {
                            var embed = new EmbedBuilder();
                            embed.Title = $"[{user.Username}] has left [**{user.Guild.Name}**]!";
                            sb.AppendLine($"{user.Mention}");
                            if (string.IsNullOrEmpty(shouldGreet.PartingMessage))
                            {
                                sb.AppendLine($"Fine, be that way! :wave:");
                            }
                            else
                            {
                                sb.AppendLine($"{shouldGreet.PartingMessage}");
                            }
                            embed.Description = sb.ToString();
                            embed.ThumbnailUrl = user.GetAvatarUrl();
                            embed.WithColor(new Color(255, 0, 0));
                            await messageChannel.SendMessageAsync("", false, embed.Build());
                        }
                    }
                    catch (Exception ex)
                    {
                        if (messageChannel != null)
                        {
                            _logger.LogError($"Error with channel -> [{messageChannel.Name}] on [{user.Guild.Name}] -> [{user.Guild.Id}] -> [{ex.Message}]");
                        }
                        else
                        {
                            _logger.LogError($"Error with no channel -> [{user.Guild.Name}] -> [{user.Guild.Id}] -> [{ex.Message}]");
                        }
                    }
                }                
            });
        }

        private async Task HandleGreeting(SocketGuildUser user)
        {
            await Task.Run(async () =>
            {
                ServerGreeting shouldGreet = GetGreeting(user);
                if (shouldGreet != null && shouldGreet.GreetUsers == true)
                {
                    var sb = new StringBuilder();   
                    ISocketMessageChannel messageChannel = null;
                    try
                    {                                             
                        if (shouldGreet.GreetingChannelId != 0)
                        {
                            messageChannel = user.Guild.GetChannel((ulong)shouldGreet.GreetingChannelId) as ISocketMessageChannel;
                        }
                        else
                        {
                            messageChannel = user.Guild.DefaultChannel as ISocketMessageChannel;
                        }
                        var embed = new EmbedBuilder();
                        embed.Title = $"[{user.Username}] has joined [**{user.Guild.Name}**]!";
                        sb.AppendLine($"{user.Mention}");
                        if (string.IsNullOrEmpty(shouldGreet.Greeting))
                        {
                            sb.AppendLine($"Welcome them! :hugging:");
                            sb.AppendLine($"(or not, :shrug:)");
                        }
                        else
                        {
                            sb.AppendLine($"{shouldGreet.Greeting}");
                        }
                        embed.Description = sb.ToString();
                        embed.ThumbnailUrl = user.GetAvatarUrl();
                        embed.WithColor(new Color(0, 255, 0));
                        await messageChannel.SendMessageAsync("", false, embed.Build());
                    }
                    catch (Exception ex)
                    {
                        if (messageChannel != null)
                        {
                            _logger.LogError($"Error with channel -> [{messageChannel.Name}] on [{user.Guild.Name}] -> [{user.Guild.Id}] -> [{ex.Message}]");
                        }
                        else
                        {
                            _logger.LogError($"Error with no channel -> [{user.Guild.Name}] -> [{user.Guild.Id}] -> [{ex.Message}]");
                        }
                    }
                }
            });
        }        

        private ServerGreeting GetGreeting(SocketGuildUser user)
        {
            ServerGreeting shouldGreet = null;
            var guildId = user.Guild.Id;
            using (var db = new NinjaBotEntities())
            {
                shouldGreet = db.ServerGreetings.Where(g => g.DiscordGuildId == (long)guildId).FirstOrDefault();
            }
            return shouldGreet;
        }
    }
}