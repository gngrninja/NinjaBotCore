using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow;
using Discord;
using Discord.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord.Net;
using Discord.WebSocket;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace NinjaBotCore.Modules.Wow
{
    public class RoseCommands : ModuleBase
    {
        private ChannelCheck _cc;        
        private readonly IConfigurationRoot _config;
        private string _prefix;
        private readonly ILogger _logger;        

        public RoseCommands(IServiceProvider services)
        {
            _logger   = services.GetRequiredService<ILogger<RoseCommands>>();
            _cc       = services.GetRequiredService<ChannelCheck>();                                                
            _config   = services.GetRequiredService<IConfigurationRoot>();
            _prefix   = _config["prefix"];            
        }

        [Command("chhelp")]
        public async Task RoseHelp()
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.Title = "Character Command Help";
            embed.ThumbnailUrl = Context.Guild.IconUrl;

            sb.AppendLine("**Add/Modify Character**");
            sb.AppendLine($":white_small_square: **{_prefix}setch** characterName itemLevel traits class mainSpec offSpec");
            sb.AppendLine();            
            sb.AppendLine($"Example: **{_prefix}setch** Oceanbreeze 850 50 Druid Resto BoomyBoi");
            sb.AppendLine();            
            sb.AppendLine("**Change Main Character**");
            sb.AppendLine($":white_small_square: **{_prefix}setmch** characterName");
            sb.AppendLine();
            sb.AppendLine($"Example: **{_prefix}setmch** Oceanbreeze");
            sb.AppendLine();
            sb.AppendLine("**Remove a Character**");
            sb.AppendLine($":white_small_square: **{_prefix}remch** characterName");
            sb.AppendLine();
            sb.AppendLine($"Example: **{_prefix}remch** Oceanbreeze");
            sb.AppendLine();
            sb.AppendLine("**List Character(s)**");
            sb.AppendLine($":white_small_square: **{_prefix}getch** @userName (or) **{_prefix}getch**");
            sb.AppendLine();
            sb.AppendLine($"Example: **{_prefix}getch** @someoneOnDiscord");
            sb.AppendLine($"Example: **{_prefix}getch**");
            sb.AppendLine();
            sb.AppendLine("**Find a Character**");
            sb.AppendLine($":white_small_square: **{_prefix}findch** characterName");
            sb.AppendLine();
            sb.AppendLine($"Example: **{_prefix}findch** Oceanbreeze");            

            embed.Description = sb.ToString();
            embed.WithColor(0, 255, 100);

            await _cc.Reply(Context, embed);
        }

        [Command("setch")]
        public async Task Addchar(string charName = null, long iLvl = 0, long traits = 0, string className = null, string mainSpec = null, string offspec = null)        
        {
            var sb = new StringBuilder();
            if (charName != null)
            {
                try 
                {
                    string changeVerb = string.Empty;
                    using (var db = new NinjaBotEntities())
                    {
                        var dbChar = db.WowMChar.Where(d => (ulong)d.ServerId == Context.Guild.Id && (ulong)d.DiscordUserId == Context.User.Id && d.CharName.ToLower() == charName.ToLower()).FirstOrDefault();
                        if (dbChar != null)
                        {
                            if (iLvl != 0)
                            {
                                dbChar.ItemLevel = iLvl;
                            }
                            if (traits != 0)
                            {
                                dbChar.Traits = traits;
                            }
                            if (!string.IsNullOrEmpty(className))
                            {
                                dbChar.ClassName = className;
                            }
                            if (!string.IsNullOrEmpty(mainSpec))
                            {
                                dbChar.MainSpec = mainSpec;
                            }
                            if (!string.IsNullOrEmpty(offspec))
                            {   
                                dbChar.OffSpec = offspec;
                            }                        
                            changeVerb = "modified";                        
                            sb.AppendLine($"{Context.User.Mention}, **{charName}** has been {changeVerb}!");
                        }
                        else
                        {
                            changeVerb = "added";
                            var allChars = db.WowMChar.Where(c => c.DiscordUserId == (long)Context.User.Id && c.ServerId == (long)Context.Guild.Id).ToList();
                            if (allChars != null && allChars.Count <= 5)
                            {
                                bool main = false;
                                if (allChars.Count == 0)
                                {   
                                    main = true;
                                }
                                await db.WowMChar.AddAsync(
                                    new WowMChar
                                    {
                                        DiscordUserId = (long)Context.User.Id,
                                        ServerId = (long)Context.Guild.Id,
                                        CharName = charName,
                                        ItemLevel = iLvl,
                                        Traits = traits,
                                        ClassName = className,
                                        MainSpec = mainSpec,
                                        OffSpec = offspec,
                                        IsMain = main
                                    });   
                                    sb.AppendLine($"{Context.User.Mention}, **{charName}** has been {changeVerb}!");                         
                            }
                            else
                            {
                                sb.AppendLine($"{Context.User.Mention}, you are at the max char limit of **6**!");
                            }                                                
                        }                    
                        await db.SaveChangesAsync();
                    }                
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error adding character -> [{ex.Message}]");
                    sb.AppendLine($"Sorry, {Context.User.Mention}, something went terribly wrong :(");
                }            
            }
            else
            {
                sb.AppendLine("You must at least specify a character name!");
            }      
            await _cc.Reply(Context, sb.ToString());
        }

        [Command("setmch")]
        public async Task SetMainChar(string charName = null)
        {            
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.Title = "Main Character Changer";
            try
            {               
                if (charName != null)                 
                {
                    using (var db = new NinjaBotEntities())
                    {
                        var newMainChar = db.WowMChar.Where(d => d.ServerId == (long)Context.Guild.Id && d.DiscordUserId == (long)Context.User.Id && d.CharName.ToLower() == charName.ToLower()).FirstOrDefault();
                        if (newMainChar == null)
                        {
                            sb.AppendLine($"Unable to find [**{charName}**]!");
                            embed.WithColor(255, 0, 0);                        
                        }
                        else
                        {
                            var curMainChar = db.WowMChar.Where(d => d.ServerId == (long)Context.Guild.Id && d.DiscordUserId == (long)Context.User.Id && d.IsMain).FirstOrDefault();
                            if (newMainChar.CharName == curMainChar.CharName)
                            {
                                sb.AppendLine($"[**{charName}**] is already your main!");
                                embed.WithColor(255, 0, 0);                        
                            }
                            else
                            {
                                newMainChar.IsMain = true;
                                curMainChar.IsMain = false;
                                await db.SaveChangesAsync();

                                sb.AppendLine($"Main character set to [**{charName}**]!");
                                embed.WithColor(0, 255 ,0);
                            }
                        }
                    }
                }
                else
                {
                    embed.WithColor(255, 0 ,0);
                    using (var db = new NinjaBotEntities())
                    {
                        sb.AppendLine("Please specify a character name!");
                        var chars = db.WowMChar.Where(d => d.DiscordUserId == (long)Context.User.Id && d.ServerId == (long)Context.Guild.Id).ToList();
                        if (chars != null && chars.Count > 0)
                        {
                            sb.AppendLine($"Your character's names are:");
                            foreach (var character in chars)
                            {
                                sb.AppendLine($":white_medium_small_square: [**{character.CharName}**]");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                embed.WithColor(255, 0, 0);
                sb.AppendLine("Sorry, something went wrong attempting to set your main character :(");
                _logger.LogError($"Error setting main char -> [{ex.Message}]");
            }
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();            
            embed.Description = sb.ToString();
            await _cc.Reply(Context, embed);
        }

        [Command("remch")]
        public async Task RemoveChar(string charName = null)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();
            if (charName != null)
            {
                try 
                {
                    WowMChar charMatch = null;
                    using (var db = new NinjaBotEntities())
                    {
                        charMatch = db.WowMChar.Where(d => d.DiscordUserId == (long)Context.User.Id && d.ServerId == (long)Context.Guild.Id && charName.ToLower() == d.CharName.ToLower()).FirstOrDefault();
                        if (charMatch != null)
                        {
                            db.WowMChar.Remove(charMatch);    
                            await db.SaveChangesAsync();
                            sb.AppendLine($"Character [**{charMatch.CharName}**] successfully removed!");
                            embed.WithColor(0, 255, 0);
                        }                        
                        else
                        {
                            sb.AppendLine($"Could not find [**{charName}**]!");
                            embed.WithColor(255, 0, 0);
                        }
                    }
                }
                catch
                {

                }
            }
            else
            {
                embed.WithColor(255, 0, 0);
                sb.AppendLine("Please specify a character name!");
                using (var db = new NinjaBotEntities())
                {
                    var chars = db.WowMChar.Where(d => d.DiscordUserId == (long)Context.User.Id && d.ServerId == (long)Context.Guild.Id).ToList();
                    if (chars != null && chars.Count > 0)
                    {
                        sb.AppendLine($"Your character's names are:");
                        foreach (var character in chars)
                        {
                            sb.AppendLine($":white_medium_small_square: [**{character.CharName}**]");
                        }
                    }
                }
            }
            embed.ThumbnailUrl = Context.User.GetAvatarUrl();
            embed.Title = "Character Removal";
            embed.Description = sb.ToString();
            await _cc.Reply(Context, embed);
        }

        [Command("getch")]
        public async Task GetChars(IUser user = null)
        {
            if (user == null)
            {
                user = Context.User;
            }
            var embed = new EmbedBuilder();
            embed.Title = $"Characters for [**{user.Username}**]";
            List<WowMChar> chars = null;
            using (var db = new NinjaBotEntities())
            {
                chars = db.WowMChar.Where(c => c.DiscordUserId == (long)user.Id && c.ServerId == (long)Context.Guild.Id).OrderByDescending(c => c.IsMain).ToList();                
            }
            if (chars != null)
            {
                
                foreach (var character in chars)
                {      
                    var sb = new StringBuilder();
                    string square = ":white_small_square:";              
                    sb.AppendLine($"{square} ilvl [**{character.ItemLevel}**]");
                    sb.AppendLine($"{square} traits [**{character.Traits}**]");
                    if (!string.IsNullOrEmpty(character.ClassName))
                    {
                        sb.AppendLine($"{square} class [**{character.ClassName}**]");
                    }
                    else
                    {
                        sb.AppendLine($"{square} class [**much empty**]");
                    }
                    if (!string.IsNullOrEmpty(character.MainSpec))
                    {
                        sb.AppendLine($"{square} ms [**{character.MainSpec}**]");
                    }
                    else
                    {
                        sb.AppendLine($"{square} ms [**much empty**]");
                    }
                    if (!string.IsNullOrEmpty(character.OffSpec))
                    {
                        sb.AppendLine($"{square} os [**{character.OffSpec}**]");
                    }
                    else
                    {
                        sb.AppendLine($"{square} os [**much empty**]");
                    }
                    string charName = string.Empty;
                    if (character.IsMain)
                    {
                        charName = $"{character.CharName} [main]";
                    }
                    else
                    {
                        charName = $"{character.CharName} [alt]";
                    }
                    embed.WithFields(
                        new EmbedFieldBuilder
                        {
                            Name = charName,
                            Value = sb.ToString(),
                            IsInline = true                            
                        }
                    );
                }
                embed.WithColor(new Color(0, 255, 155));
            }
            else 
            {
                embed.Description = "No chars found!";
                embed.WithColor(new Color(255, 0, 0));
            }
            embed.ThumbnailUrl = user.GetAvatarUrl();
            embed.WithFooter(
                new EmbedFooterBuilder
                {
                    Text = $"Character info requested by [{Context.User.Username}]",
                    IconUrl = Context.User.GetAvatarUrl()                    
                }
            );            
            await _cc.Reply(Context, embed);
        }
 
        [Command("findch")]
        public async Task FindChar(string charName = null)
        {
            var sb = new StringBuilder();
            var embed = new EmbedBuilder();
            embed.Title = $"Character Finder";
            if (charName != null)
            {
                embed.Title = $"Character Finder [{charName}]";
                WowMChar findMe = null;
                using (var db = new NinjaBotEntities())
                {
                    findMe = db.WowMChar.Where(d => d.ServerId == (long)Context.Guild.Id && d.CharName.ToLower().Contains(charName)).FirstOrDefault();
                    if (findMe != null)
                    {
                        var fb = new StringBuilder();
                        string square = ":white_small_square:";                        
                        fb.AppendLine($"{square} ilvl [**{findMe.ItemLevel}**]");
                        fb.AppendLine($"{square} traits [**{findMe.Traits}**]");
                        if (!string.IsNullOrEmpty(findMe.ClassName))
                        {
                            fb.AppendLine($"{square} class [**{findMe.ClassName}**]");
                        }
                        else
                        {
                            fb.AppendLine($"{square} class [**much empty**]");
                        }
                        if (!string.IsNullOrEmpty(findMe.MainSpec))
                        {
                            fb.AppendLine($"{square} ms [**{findMe.MainSpec}**]");
                        }
                        else
                        {
                            fb.AppendLine($"{square} ms [**much empty**]");
                        }
                        if (!string.IsNullOrEmpty(findMe.OffSpec))
                        {
                            fb.AppendLine($"{square} os [**{findMe.OffSpec}**]");
                        }
                        else
                        {
                            fb.AppendLine($"{square} os [**much empty**]");
                        }
                        string foundCharName = string.Empty;
                        if (findMe.IsMain)
                        {
                            foundCharName = $"{findMe.CharName} [main]";
                        }
                        else
                        {
                            foundCharName = $"{findMe.CharName} [alt]";
                        }
                        embed.WithFields(
                            new EmbedFieldBuilder
                            {
                                Name = foundCharName,
                                Value = fb.ToString(),
                                IsInline = true                            
                            }
                        );
                        var belongsTo = await Context.Guild.GetUserAsync((ulong)findMe.DiscordUserId);                        
                        sb.AppendLine($"This character belongs to [**{belongsTo.Username}**]");
                        embed.WithColor(0, 200, 100);
                        embed.ThumbnailUrl = belongsTo.GetAvatarUrl();                        
                    }
                    else
                    {
                        sb.AppendLine($"Sorry {Context.User.Mention}, no results :(");
                        embed.WithColor(255, 100, 0);
                    }
                }
            }
            else
            {
                embed.WithColor(255, 0 ,0 );
                sb.AppendLine("You must specify a character name!");
            }
            embed.Description = sb.ToString();
            embed.WithFooter(
                new EmbedFooterBuilder
                {
                    Text = $"Lookup performed by [{Context.User.Username}]",
                    IconUrl = Context.User.GetAvatarUrl()      
                }            
            );
            await _cc.Reply(Context, embed);
        }
    }
}
