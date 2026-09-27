using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.Net.Rest;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;

namespace NinjaBotCore.Tests;

public class RaidRecapAccessTests
{
    [Fact]
    public void ShardedContextInterfaceExposesTheSocketShard()
    {
        using var h = new Harness();
        Assert.Same(h.Client, h.Context.Client);
        Assert.Same(h.Client.GetShard(0), ((IInteractionContext)h.Context).Client);
        Assert.IsType<DiscordSocketClient>(((IInteractionContext)h.Context).Client);
    }

    [Fact]
    public async Task RestGuildChildrenRetainDownloadedRolesForPermissionResolution()
    {
        using var h = new Harness();
        var guild = await h.Client.GetShard(0).Rest.GetGuildAsync(Harness.GuildId);
        var channel = await guild.GetTextChannelAsync(Harness.ChannelId);
        var actor = await guild.GetUserAsync(Harness.ActorId);
        Assert.Same(guild, ((IGuildUser)actor).Guild);
        Assert.Same(guild, ((IGuildChannel)channel).Guild);
        Assert.True(actor.GetPermissions(channel).ViewChannel);
        Assert.True(actor.GetPermissions(channel).SendMessages);
        Assert.Equal(new[] { "guilds/100", "channels/200", "guilds/100/members/300" }, h.Rest.Requests);
    }

    [Fact]
    public async Task RealShardedContextAllowsMembersUsingFreshRestPermissions()
    {
        using var h = new Harness();
        Assert.Equal(new RaidRecapAccess(true, true), await h.Adapter.AccessAsync(h.Context));
        Assert.Equal(Harness.AccessRequests, h.Rest.Requests);
    }

    [Theory]
    [InlineData(300UL, 1024UL, false, false)]
    [InlineData(400UL, 1024UL, false, false)]
    [InlineData(300UL, 2048UL, true, false)]
    [InlineData(400UL, 2048UL, true, false)]
    public async Task FreshMemberOverwriteCanRevokeViewOrShare(ulong member, ulong denied, bool view, bool share)
    {
        using var h = new Harness();
        Assert.Equal(new RaidRecapAccess(true, true), await h.Adapter.AccessAsync(h.Context));
        h.Rest.Overwrites = new JArray(new JObject { ["id"] = member.ToString(), ["type"] = 1, ["allow"] = "0", ["deny"] = denied.ToString() });
        Assert.Equal(new RaidRecapAccess(view, share), await h.Adapter.AccessAsync(h.Context));
        Assert.Equal(Harness.AccessRequests.Concat(Harness.AccessRequests), h.Rest.Requests);
    }

    [Theory]
    [InlineData(300UL)]
    [InlineData(400UL)]
    public async Task MissingMemberFailsClosed(ulong member)
    {
        using var h = new Harness();
        h.Rest.MissingMember = member;
        Assert.Equal(new RaidRecapAccess(false, false), await h.Adapter.AccessAsync(h.Context));
        Assert.Contains($"guilds/100/members/{member}", h.Rest.Requests);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(15)]
    public async Task ThreadsAndNonTextChannelsFailClosed(int channelType)
    {
        using var h = new Harness();
        h.Rest.ChannelType = channelType;
        Assert.Equal(new RaidRecapAccess(false, false), await h.Adapter.AccessAsync(h.Context));
        Assert.Contains("channels/200", h.Rest.Requests);
        Assert.DoesNotContain(h.Rest.Requests, p => p.Contains("/members/"));
    }

    [Fact]
    public async Task RoleRevocationIsNotReadFromGatewayOrPreviousRestSnapshot()
    {
        using var h = new Harness();
        Assert.Equal(new RaidRecapAccess(true, true), await h.Adapter.AccessAsync(h.Context));
        h.Rest.RolePermissions = 0;
        Assert.Equal(new RaidRecapAccess(false, false), await h.Adapter.AccessAsync(h.Context));
        Assert.Equal(Harness.AccessRequests.Concat(Harness.AccessRequests), h.Rest.Requests);
    }

    [Theory]
    [InlineData(300UL)]
    [InlineData(400UL)]
    public async Task ActiveFreshTimeoutAllowsViewingButDeniesPublicSend(ulong member)
    {
        using var h = new Harness();
        h.Rest.Timeouts[member] = DateTimeOffset.UtcNow.AddHours(1);
        var guild = await h.Client.GetShard(0).Rest.GetGuildAsync(Harness.GuildId);
        var user = await guild.GetUserAsync(member);
        var channel = await guild.GetTextChannelAsync(Harness.ChannelId);
        Assert.True(user.TimedOutUntil > DateTimeOffset.UtcNow);
        // Discord.Net 3.18 resolves role permissions without applying member timeout.
        Assert.True(user.GetPermissions(channel).SendMessages);
        Assert.Equal(new RaidRecapAccess(true, false), await h.Adapter.AccessAsync(h.Context));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Adapter.PublishAsync(h.Context, PublicPayload(), () => true));
        Assert.Equal(0, h.Rest.Writes);
    }

    [Theory]
    [InlineData(300UL, false)]
    [InlineData(300UL, true)]
    [InlineData(400UL, false)]
    [InlineData(400UL, true)]
    public async Task AbsentOrExpiredTimeoutStillAllowsPublicSend(ulong member, bool expired)
    {
        using var h = new Harness();
        h.Rest.Timeouts[member] = expired ? DateTimeOffset.UtcNow.AddHours(-1) : null;
        Assert.Equal(new RaidRecapAccess(true, true), await h.Adapter.AccessAsync(h.Context));
        Assert.Equal(700UL, await h.Adapter.PublishAsync(h.Context, PublicPayload(), () => true));
        Assert.Equal(1, h.Rest.Writes);
    }

    [Fact]
    public async Task TimeoutImposedDuringFinalMemberLookupPreventsPublicSend()
    {
        using var h = new Harness();
        Assert.Equal(new RaidRecapAccess(true, true), await h.Adapter.AccessAsync(h.Context));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Rest.BeforeMember = async id =>
        {
            if (id != Harness.ActorId) return;
            entered.TrySetResult();
            await release.Task;
        };
        var publish = h.Adapter.PublishAsync(h.Context, PublicPayload(), () => true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Rest.Timeouts[Harness.ActorId] = DateTimeOffset.UtcNow.AddHours(1);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => publish);
        Assert.Equal(0, h.Rest.Writes);
        Assert.Equal(Harness.AccessRequests.Concat(Harness.AccessRequests), h.Rest.Requests);
    }

    [Fact]
    public async Task PlayerPayloadUsesActualSdkRestSerializationIncludingSectionAccessoriesOffline()
    {
        using var h=new Harness();
        var (service,source,players,s)=RaidRecapPlayerFlowTests.Setup(100);
        await service.ApplyAsync(s,"players",null);await service.ApplyAsync(s,"player_pull","1");await service.ApplyAsync(s,"player","1");
        var payload=NinjaBotCore.Modules.Interactions.Wow.RaidRecapView.Build(s);
        // The fixture IRestClient is the only transport. This exercises wire serialization,
        // not public sharing; no network, gateway login or real Discord send is possible.
        await h.Context.Channel.SendMessageAsync(components:payload,flags:MessageFlags.ComponentsV2,allowedMentions:AllowedMentions.None,options:new RequestOptions{RetryMode=RetryMode.AlwaysFail});
        var wire=JObject.Parse(h.Rest.LastWrite);
        Assert.Equal((int)MessageFlags.ComponentsV2,(int)wire["flags"]);Assert.Equal(2,wire["components"].Count());
        Assert.All(wire["components"],c=>Assert.Equal(17,(int)c["type"]));
        Assert.Empty(wire["allowed_mentions"]["parse"]);Assert.True(wire["embeds"]==null || !wire["embeds"].Any());
        var sections=wire.SelectTokens("$..accessory").ToArray();Assert.Equal(3,sections.Length);Assert.All(sections,b=>Assert.Equal(2,(int)b["type"]));
        Assert.Contains(sections,b=>(string)b["label"]=="Change pull");Assert.Contains(sections,b=>(string)b["label"]=="Player on WarcraftLogs");Assert.Contains(sections,b=>(string)b["label"]=="How to read");
        var dir=Environment.GetEnvironmentVariable("RAID_RECAP_EVIDENCE_DIR");
        if(!string.IsNullOrEmpty(dir)){Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"synthetic-player-discord-rest-wire.json"),wire.ToString());}
    }

    internal static async Task<JObject> CaptureOfflineWire(MessageComponent payload)
    {
        using var h=new Harness();
        await h.Context.Channel.SendMessageAsync(components:payload,flags:MessageFlags.ComponentsV2,allowedMentions:AllowedMentions.None,options:new RequestOptions{RetryMode=RetryMode.AlwaysFail});
        Assert.Equal(1,h.Rest.Writes);
        var wire=JObject.Parse(h.Rest.LastWrite);
        Assert.Equal((int)MessageFlags.ComponentsV2,(int)wire["flags"]);
        Assert.Empty(wire["allowed_mentions"]["parse"]);
        Assert.True(wire["content"]==null || string.IsNullOrEmpty((string)wire["content"]));
        Assert.True(wire["embeds"]==null || !wire["embeds"].Any());
        return wire;
    }

    private static MessageComponent PublicPayload() =>
        new ComponentBuilderV2().AddComponent(new TextDisplayBuilder("Offline timeout regression fixture")).Build();

    // The actual SDK context, REST deserializer, entities and permission resolver run offline.
    // Reflection only constructs gateway-owned identities and sets offline REST state; no login,
    // gateway connection, live token, HTTP client or replacement AccessAsync is involved.
    private sealed class Harness : IDisposable
    {
        public const ulong GuildId = 100, ChannelId = 200, ActorId = 300, BotId = 400;
        public static readonly string[] AccessRequests = { "guilds/100", "channels/200", "guilds/100/members/300", "guilds/100/members/400" };
        public readonly FixtureRest Rest = new();
        public readonly DiscordShardedClient Client;
        public readonly ShardedInteractionContext Context;
        public readonly RaidRecapDiscord Adapter = new(Mock.Of<IServiceScopeFactory>(MockBehavior.Strict));

        public Harness()
        {
            Client = new DiscordShardedClient(new DiscordSocketConfig
            {
                TotalShards = 1,
                RestClientProvider = _ => Rest,
                WebSocketProvider = () =>
                {
                    var socket = new Mock<Discord.Net.WebSockets.IWebSocketClient>(MockBehavior.Strict);
                    socket.Setup(x => x.Dispose());
                    return socket.Object;
                },
                DefaultRetryMode = RetryMode.AlwaysFail
            });
            var shard = Client.GetShard(0);
            var guild = Construct<SocketGuild>(shard, GuildId);
            var globalType = typeof(SocketUser).Assembly.GetType("Discord.WebSocket.SocketGlobalUser", true);
            var actor = Construct<SocketGuildUser>(guild, Construct(globalType, shard, ActorId));
            var bot = Construct<SocketSelfUser>(shard, Construct(globalType, shard, BotId));
            typeof(BaseDiscordClient).GetProperty("CurrentUser").SetValue(shard, bot);
            foreach (var rest in new[] { Client.Rest, shard.Rest })
            {
                var api = typeof(BaseDiscordClient).GetProperty("ApiClient", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rest);
                var state = api.GetType().GetProperty("LoginState");
                state.DeclaringType.GetProperty("LoginState").SetValue(api, LoginState.LoggedIn);
            }
            var channel = Construct<SocketTextChannel>(shard, ChannelId, guild);
            var ctor = typeof(SocketSlashCommand).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var model = JsonConvert.DeserializeObject("""
                {"id":"500","application_id":"400","type":2,"guild_id":"100","channel_id":"200",
                 "data":{"id":"600","type":1,"name":"raid-recap"},"token":"offline-not-a-token","version":1}
                """, ctor.GetParameters()[1].ParameterType, new JsonSerializerSettings
                {
                    ContractResolver = (Newtonsoft.Json.Serialization.IContractResolver)Activator.CreateInstance(
                        typeof(DiscordRestClient).Assembly.GetType("Discord.Net.Converters.DiscordContractResolver", true))
                });
            var interaction = (SocketSlashCommand)ctor.Invoke(new[] { shard, model, channel, actor });
            Context = new ShardedInteractionContext(Client, interaction);
        }

        private static T Construct<T>(params object[] args) => (T)Construct(typeof(T), args);
        private static object Construct(Type type, params object[] args) => Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.NonPublic, null, args, null);
        public void Dispose() => Client.Dispose();
    }

    private sealed class FixtureRest : IRestClient
    {
        public readonly List<string> Requests = new();
        public JArray Overwrites = new();
        public ulong RolePermissions = 3072;
        public ulong? MissingMember;
        public int ChannelType;
        public readonly Dictionary<ulong, DateTimeOffset?> Timeouts = new();
        public Func<ulong, Task> BeforeMember;
        public int Writes;
        public string LastWrite;
        public void Dispose() { }
        public void SetHeader(string key, string value) { }
        public void SetCancelToken(CancellationToken token) { }
        public async Task<RestResponse> SendAsync(string method, string endpoint, CancellationToken token, bool headerOnly = false,
            string reason = null, IEnumerable<KeyValuePair<string, IEnumerable<string>>> requestHeaders = null)
        {
            Assert.Equal("GET", method);
            var path = endpoint.TrimStart('/').Split('?')[0];
            Requests.Add(path);
            var status = HttpStatusCode.OK;
            JObject json;
            switch (path)
            {
                case "guilds/100":
                    json = JObject.Parse("""
                        {"id":"100","name":"Synthetic guild","owner_id":"999","preferred_locale":"en-US",
                         "features":[],"roles":[{"id":"100","name":"@everyone","permissions":"0","position":0},
                         {"id":"101","name":"Member","permissions":"0","position":1}]}
                        """);
                    json["roles"][1]["permissions"] = RolePermissions.ToString();
                    break;
                case "channels/200":
                    json = JObject.Parse("""
                        {"id":"200","guild_id":"100","name":"synthetic-channel","type":0,"position":0,
                         "parent_id":"201","owner_id":"300","message_count":0,"member_count":2,
                         "thread_metadata":{"archived":false,"auto_archive_duration":1440,"archive_timestamp":"2026-01-01T00:00:00Z","locked":false}}
                        """);
                    json["type"] = ChannelType;
                    json["permission_overwrites"] = Overwrites.DeepClone();
                    break;
                case "guilds/100/members/300":
                case "guilds/100/members/400":
                    var id = ulong.Parse(path.Split('/').Last());
                    if (BeforeMember != null) await BeforeMember(id);
                    if (MissingMember == id)
                    {
                        status = HttpStatusCode.NotFound;
                        json = JObject.Parse("{\"code\":10007,\"message\":\"Unknown Member\"}");
                    }
                    else json = new JObject
                    {
                        ["user"] = new JObject { ["id"] = id.ToString(), ["username"] = "synthetic-member", ["discriminator"] = "0000", ["bot"] = id == Harness.BotId },
                        ["communication_disabled_until"] = Timeouts.GetValueOrDefault(id)?.ToString("O"),
                        ["roles"] = new JArray("101"), ["joined_at"] = "2026-01-01T00:00:00Z", ["deaf"] = false, ["mute"] = false
                    };
                    break;
                default: throw new InvalidOperationException("Unexpected offline REST route: " + path);
            }
            return new RestResponse(status, new Dictionary<string, string>(), new MemoryStream(Encoding.UTF8.GetBytes(json.ToString())));
        }
        public Task<RestResponse> SendAsync(string method, string endpoint, string json, CancellationToken token, bool headerOnly = false,
            string reason = null, IEnumerable<KeyValuePair<string, IEnumerable<string>>> requestHeaders = null)
        {
            Assert.Equal("POST", method);
            Assert.Equal("channels/200/messages", endpoint.TrimStart('/'));
            Writes++; LastWrite=json;
            const string response = """
                {"id":"700","channel_id":"200","author":{"id":"400","username":"fixture-bot","discriminator":"0000","bot":true},"content":"","timestamp":"2026-01-01T00:00:00Z","tts":false,"mention_everyone":false,"mentions":[],"mention_roles":[],"attachments":[],"embeds":[],"pinned":false,"type":0}
                """;
            return Task.FromResult(new RestResponse(HttpStatusCode.OK, new Dictionary<string, string>(), new MemoryStream(Encoding.UTF8.GetBytes(response))));
        }
        public Task<RestResponse> SendAsync(string method, string endpoint, IReadOnlyDictionary<string, object> multipartParams, CancellationToken token,
            bool headerOnly = false, string reason = null, IEnumerable<KeyValuePair<string, IEnumerable<string>>> requestHeaders = null) => throw new InvalidOperationException("No writes in fixture");
    }
}
