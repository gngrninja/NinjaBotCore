using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Net;
using Discord.Commands;
using Discord.WebSocket;
using NinjaBotCore.Models.YouTube;
using Google.Apis.YouTube;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;

namespace NinjaBotCore.Modules.YouTube
{
    public class YouTubeCommands : ModuleBase
    {
        private static ChannelCheck _cc = null;
        private static YouTubeApi _youTubeApi = null;

        public YouTubeCommands(ChannelCheck cc)
        {
            if (_cc == null)
            {
                _cc = cc;
            }
            if (_youTubeApi == null)
            {
                _youTubeApi = new YouTubeApi();
            }
        }
        //Command that links just one video normally so it has play button
        [Command("ysearch")]
        [Summary("Search YouTube for a specific keyword")]
        public async Task SearchYouTube([Remainder] string args = "")
        {
            string searchFor = string.Empty;
            var embed = new EmbedBuilder();
            var embedThumb = Context.User.GetAvatarUrl();
            StringBuilder sb = new StringBuilder();
            List<Google.Apis.YouTube.v3.Data.SearchResult> results = null;

            embed.ThumbnailUrl = embedThumb;

            if (string.IsNullOrEmpty(args))
            {
                
                embed.Title = $"No search term provided!";
                embed.WithColor(new Discord.Color(255, 0, 0));                 
                sb.AppendLine("Please provide a term to search for!");
                embed.Description = sb.ToString();
                await _cc.Reply(Context, embed);
                return;
            }
            else
            {
                searchFor = args;                                
                embed.WithColor(new Color(0, 255, 0));                
                results = await _youTubeApi.SearchChannelsAsync(searchFor);
            }

            if (results != null)
            {
                string videoUrlPrefix = $"https://www.youtube.com/watch?v=";
                embed.Title = $"YouTube Search For (**{searchFor}**)";
                var thumbFromVideo = results.Where(r => r.Id.Kind == "youtube#video").Take(1).FirstOrDefault();
                if (thumbFromVideo != null)
                {
                    embed.ThumbnailUrl = thumbFromVideo.Snippet.Thumbnails.Default__.Url;
                }                
                foreach (var result in results.Where(r => r.Id.Kind == "youtube#video").Take(3))
                {
                    string fullVideoUrl = string.Empty;
                    string videoId = string.Empty;
                    string description = string.Empty;
                    if (string.IsNullOrEmpty(result.Snippet.Description))
                    {
                        description = "No description available.";
                    }
                    else
                    {
                        description = result.Snippet.Description;
                    }
                    if (result.Id.VideoId != null)
                    {
                        fullVideoUrl = $"{videoUrlPrefix}{result.Id.VideoId.ToString()}";
                    }
                    sb.AppendLine($":video_camera: **__{result.Snippet.ChannelTitle}__** -> [**{result.Snippet.Title}**]({fullVideoUrl})\n\n *{description}*\n");              
                }
                embed.Description = sb.ToString();
                await _cc.Reply(Context, embed);
            }
        }
    }
}
