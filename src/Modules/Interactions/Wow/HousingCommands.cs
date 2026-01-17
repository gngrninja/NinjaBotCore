using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NinjaBotCore.Models.Wow.Housing;
using NinjaBotCore.Modules.Wow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// Housing-related commands for WoW's housing feature.
    /// </summary>
    public class HousingCommands : NinjaBotBaseModule
    {
        private readonly ILogger<HousingCommands> _logger;
        private readonly WowApi _wowApi;

        public HousingCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<HousingCommands> logger,
            WowApi wowApi)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowApi = wowApi;
        }

        [SlashCommand("housing-random-decor", "Get a random housing decor item")]
        public async Task GetRandomDecor(
            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                var url = "/data/wow/search/decor?namespace=static-us&_page=1&_pageSize=1000";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us");
                var decorSearch = JsonConvert.DeserializeObject<DecorSearchResponse>(response);

                if (decorSearch?.Results == null || decorSearch.Results.Count == 0)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("No Decor Items Found")
                        .WithDescription("Unable to fetch housing decor items at this time.")
                        .WithColor(Color.Red)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                var random = new Random();
                var randomDecor = decorSearch.Results[random.Next(decorSearch.Results.Count)];

                var embed = new EmbedBuilder()
                    .WithTitle($"🏠 {randomDecor.Data.Name.EnUS}")
                    .WithColor(new Color(0, 176, 240))
                    .AddField("Decor ID", randomDecor.Data.Id, inline: true);

                if (randomDecor.Data.Item != null)
                {
                    var itemId = randomDecor.Data.Item.Id;
                    var itemName = randomDecor.Data.Item.Name?.EnUS ?? "Unknown Item";
                    var wowheadUrl = $"https://www.wowhead.com/item={itemId}";

                    embed.AddField("Item", $"[{itemName}]({wowheadUrl})", inline: true)
                        .AddField("Item ID", itemId, inline: true);

                    try
                    {
                        var itemUrl = $"/data/wow/item/{itemId}?namespace=static-us";
                        var itemResponse = await _wowApi.GetAPIRequestAsync(itemUrl, "en_US", "us");
                        var itemData = JsonConvert.DeserializeObject<dynamic>(itemResponse);

                        if (itemData?.quality?.name != null)
                        {
                            string qualityName = itemData.quality.name.ToString();
                            var qualityEmoji = GetQualityEmoji(qualityName);
                            if (qualityEmoji != null)
                            {
                                embed.AddField("Quality", $"{qualityEmoji} {qualityName}", inline: true);
                            }
                        }

                        var mediaUrl = $"/data/wow/media/item/{itemId}?namespace=static-us";
                        var mediaResponse = await _wowApi.GetAPIRequestAsync(mediaUrl, "en_US", "us");
                        var mediaData = JsonConvert.DeserializeObject<dynamic>(mediaResponse);

                        var assets = (IEnumerable<dynamic>)mediaData.assets;
                        var renderAsset = assets.FirstOrDefault(a => (string)a.key == "render")
                            ?? assets.FirstOrDefault(a => (string)a.key == "icon");

                        if (renderAsset?.value != null)
                        {
                            embed.WithImageUrl((string)renderAsset.value);
                        }
                    }
                    catch { /* Continue without extra details */ }
                }

                long estimatedTotal = decorSearch.PageCount > 1
                    ? decorSearch.PageCount * decorSearch.PageSize
                    : decorSearch.Results.Count;

                embed.WithFooter(decorSearch.ResultCountCapped
                    ? $"Showing {decorSearch.Results.Count} of {estimatedTotal:N0}+ decor items"
                    : $"Showing {decorSearch.Results.Count} of ~{estimatedTotal:N0} total decor items");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching random decor");
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription("An error occurred while fetching random decor item.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
            }
        }

        [SlashCommand("housing-search-decor", "Search for housing decor items")]
        public async Task SearchDecor(
            [Summary("name", "Decor item name to search for")]
            string name,
            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                var encodedName = Uri.EscapeDataString(name);
                var url = $"/data/wow/search/decor?namespace=static-us&name.en_US={encodedName}&_pageSize=10";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us");
                var decorSearch = JsonConvert.DeserializeObject<DecorSearchResponse>(response);

                if (decorSearch?.Results == null || decorSearch.Results.Count == 0)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("No Results Found")
                        .WithDescription($"No decor items found matching '{name}'")
                        .WithColor(Color.Orange)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle($"🔍 Decor Search: {name}")
                    .WithColor(new Color(0, 176, 240));

                var sb = new StringBuilder();
                foreach (var result in decorSearch.Results.Take(10))
                {
                    sb.Append($"🏠 **{result.Data.Name.EnUS}** (ID: {result.Data.Id})");
                    if (result.Data.Item != null)
                    {
                        var wowheadUrl = $"https://www.wowhead.com/item={result.Data.Item.Id}";
                        sb.Append($" — [Item: {result.Data.Item.Name?.EnUS ?? "Unknown"}]({wowheadUrl})");
                    }
                    sb.AppendLine();
                }

                embed.WithDescription(sb.ToString());
                embed.WithFooter($"Showing {Math.Min(10, decorSearch.Results.Count)} of {decorSearch.Results.Count} results");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching decor");
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription("An error occurred while searching for decor items.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
            }
        }

        [SlashCommand("housing-list-rooms", "List all available housing rooms")]
        public async Task ListRooms(
            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                var url = "/data/wow/room/index?namespace=static-us";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us");
                var roomIndex = JsonConvert.DeserializeObject<RoomIndexResponse>(response);

                if (roomIndex?.Rooms == null || roomIndex.Rooms.Count == 0)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("No Rooms Found")
                        .WithDescription("Unable to fetch housing rooms at this time.")
                        .WithColor(Color.Red)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                var validRooms = roomIndex.Rooms
                    .Where(r => r.Id > 0 && !string.IsNullOrWhiteSpace(r.Name))
                    .OrderBy(r => r.Name)
                    .ToList();

                var embed = new EmbedBuilder()
                    .WithTitle("🏡 Available Housing Rooms")
                    .WithColor(new Color(92, 184, 92));

                var sb = new StringBuilder();
                foreach (var room in validRooms)
                {
                    sb.AppendLine($"🚪 **{room.Name}** (ID: {room.Id})");
                }

                embed.WithDescription(sb.ToString());
                embed.WithFooter($"Total rooms: {validRooms.Count}");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching room list");
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription("An error occurred while fetching room list.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
            }
        }

        [SlashCommand("housing-random-room", "Get details about a random housing room")]
        public async Task GetRandomRoom(
            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                var url = "/data/wow/room/index?namespace=static-us";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us");
                var roomIndex = JsonConvert.DeserializeObject<RoomIndexResponse>(response);

                if (roomIndex?.Rooms == null || roomIndex.Rooms.Count == 0)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("No Rooms Found")
                        .WithDescription("Unable to fetch housing rooms at this time.")
                        .WithColor(Color.Red)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                var random = new Random();
                var randomRoom = roomIndex.Rooms[random.Next(roomIndex.Rooms.Count)];

                var detailUrl = $"/data/wow/room/{randomRoom.Id}?namespace=static-us";
                var detailResponse = await _wowApi.GetAPIRequestAsync(detailUrl, "en_US", "us");
                var roomDetail = JsonConvert.DeserializeObject<RoomResponse>(detailResponse);

                var embed = new EmbedBuilder()
                    .WithTitle($"🏡 {randomRoom.Name}")
                    .WithColor(new Color(92, 184, 92))
                    .AddField("Room ID", randomRoom.Id, inline: true);

                if (roomDetail != null && !string.IsNullOrEmpty(roomDetail.Name))
                {
                    embed.WithDescription($"**{roomDetail.Name}**");
                }

                try
                {
                    var mediaUrl = $"/data/wow/media/room/{randomRoom.Id}?namespace=static-us";
                    var mediaResponse = await _wowApi.GetAPIRequestAsync(mediaUrl, "en_US", "us");
                    var mediaData = JsonConvert.DeserializeObject<dynamic>(mediaResponse);

                    if (mediaData?.assets != null && mediaData.assets.Count > 0)
                    {
                        string imageUrl = mediaData.assets[0].value?.ToString();
                        if (!string.IsNullOrEmpty(imageUrl))
                        {
                            embed.WithImageUrl(imageUrl);
                        }
                    }
                }
                catch { /* Room media likely doesn't exist */ }

                embed.WithFooter($"Total rooms available: {roomIndex.Rooms.Count}");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching random room");
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription("An error occurred while fetching random room.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
            }
        }

        [SlashCommand("housing-search-fixtures", "Search for housing fixtures")]
        public async Task SearchFixtures(
            [Summary("name", "Fixture name to search for")]
            string name,
            [Summary("public", "Show results publicly (default: private)")]
            bool publicDisplay = false)
        {
            await DeferAsync(ephemeral: !publicDisplay);

            try
            {
                var encodedName = Uri.EscapeDataString(name);
                var url = $"/data/wow/search/fixture?namespace=static-us&name.en_US={encodedName}&_pageSize=10";
                var response = await _wowApi.GetAPIRequestAsync(url, "en_US", "us");
                var fixtureSearch = JsonConvert.DeserializeObject<FixtureSearchResponse>(response);

                if (fixtureSearch?.Results == null || fixtureSearch.Results.Count == 0)
                {
                    await FollowupAsync(embed: new EmbedBuilder()
                        .WithTitle("No Results Found")
                        .WithDescription($"No fixtures found matching '{name}'")
                        .WithColor(Color.Orange)
                        .Build(), ephemeral: !publicDisplay);
                    return;
                }

                var embed = new EmbedBuilder()
                    .WithTitle($"🔧 Fixture Search: {name}")
                    .WithColor(new Color(217, 83, 79));

                var sb = new StringBuilder();
                foreach (var result in fixtureSearch.Results.Take(10))
                {
                    sb.AppendLine($"🔧 **{result.Data.Name.EnUS}** (ID: {result.Data.Id})");
                }

                embed.WithDescription(sb.ToString());
                embed.WithFooter($"Showing {Math.Min(10, fixtureSearch.Results.Count)} of {fixtureSearch.Results.Count} results");

                await FollowupAsync(embed: embed.Build(), ephemeral: !publicDisplay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching fixtures");
                await FollowupAsync(embed: new EmbedBuilder()
                    .WithTitle("Error")
                    .WithDescription("An error occurred while searching for fixtures.")
                    .WithColor(Color.Red)
                    .Build(), ephemeral: !publicDisplay);
            }
        }

        private static string GetQualityEmoji(string qualityName) => qualityName?.ToLower() switch
        {
            "legendary" => "🟠",
            "artifact" => "🟠",
            "epic" => "🟣",
            "rare" => "🔵",
            "uncommon" => "🟢",
            "common" => "⚪",
            _ => null
        };
    }
}
