using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;
namespace NinjaBotCore.Tests;
public class RaidRecapCommandTests
{
    private sealed class Harness
    {
        public readonly Mock<IDiscordInteraction> Interaction=new();
        public readonly Mock<IInteractionContext> Context=new();
        public readonly Mock<IRaidRecapDiscord> Discord=new(MockBehavior.Strict);
        public readonly Mock<IRaidRecapSource> Source=new(MockBehavior.Strict);
        public readonly RaidRecapSessions Sessions;
        public readonly RaidRecapCommands Module;
        public bool Deferred;
        public MessageProperties Edited;
        public readonly Mock<IMessageChannel> Channel=new();
        public Harness(Func<DateTimeOffset> clock=null,bool concretePublication=false,int capacity=256)
        {
            Sessions=new RaidRecapSessions(clock,capacity);
            var user=new Mock<IUser>();user.SetupGet(x=>x.Id).Returns(1);
            var guild=new Mock<IGuild>();guild.SetupGet(x=>x.Id).Returns(2);
            var channel=Channel;channel.SetupGet(x=>x.Id).Returns(3);
            channel.SetReturnsDefault(Task.FromResult(Mock.Of<IUserMessage>(m=>m.Id==42)));
            Context.SetupGet(x=>x.User).Returns(user.Object);Context.SetupGet(x=>x.Guild).Returns(guild.Object);
            Context.SetupGet(x=>x.Channel).Returns(channel.Object);Context.SetupGet(x=>x.Interaction).Returns(Interaction.Object);
            Interaction.Setup(x=>x.DeferAsync(It.IsAny<bool>(),It.IsAny<RequestOptions>())).Callback<bool,RequestOptions>((_,_)=>Deferred=true).Returns(Task.CompletedTask);
            Interaction.Setup(x=>x.ModifyOriginalResponseAsync(It.IsAny<Action<MessageProperties>>(),It.IsAny<RequestOptions>())).Callback<Action<MessageProperties>,RequestOptions>((act,_)=> { Edited=new MessageProperties();act(Edited); }).ReturnsAsync((RestInteractionMessage)null);
            Discord.Setup(x=>x.AccessAsync(Context.Object)).Returns(()=> { Assert.True(Deferred);return Task.FromResult(new RaidRecapAccess(true,true)); });
            IRaidRecapDiscord adapter=concretePublication
                ?new RaidRecapDiscord(Mock.Of<IServiceScopeFactory>(MockBehavior.Strict),ctx=>Discord.Object.AccessAsync(ctx))
                :Discord.Object;
            Module=new RaidRecapCommands(new RaidRecapService(Source.Object,new RaidRecapCache()),Sessions,adapter,NullLogger<RaidRecapCommands>.Instance);
            typeof(InteractionModuleBase<IInteractionContext>).GetProperty("Context").SetValue(Module,Context.Object);
        }
    }
    [Fact]
    public async Task DirectSlashDefersBeforeProviderAndProducesPrivateV2WithoutMentions()
    {
        var h=new Harness();h.Source.Setup(x=>x.GetRaidRecapReportAsync("AbCdEfGh12345678")).Returns(()=> { Assert.True(h.Deferred);return Task.FromResult(RaidRecapPanelTests.Report()); });
        await h.Module.StartAsync(report:"AbCdEfGh12345678");
        h.Interaction.Verify(x=>x.DeferAsync(true,It.IsAny<RequestOptions>()),Times.Once);
        Assert.NotNull(h.Edited);Assert.Equal(MessageFlags.ComponentsV2,h.Edited.Flags.Value);
        Assert.Same(AllowedMentions.None,h.Edited.AllowedMentions.Value);
        Assert.Equal("",h.Edited.Content.Value);Assert.Null(h.Edited.Embed.Value);
        Assert.Contains("Overview",RaidRecapPanelTests.Text(h.Edited.Components.Value));
        h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
    }
    [Fact]
    public async Task AssociatedGuildIsResolvedOnlyAfterDeferral()
    {
        var h=new Harness();h.Discord.Setup(x=>x.GuildAsync(h.Context.Object)).Returns(()=>{ Assert.True(h.Deferred); return Task.FromResult(new RaidRecapGuild("Guild","realm","us")); });
        h.Source.Setup(x=>x.GetRaidRecapReportsAsync("Guild","realm","us")).ReturnsAsync(Array.Empty<NinjaBotCore.Models.Wow.WclV2Report>());
        await h.Module.StartAsync();Assert.Contains("No reports",RaidRecapPanelTests.Text(h.Edited.Components.Value));
    }
    [Fact]
    public async Task OverrideDoesNotUseAssociatedRealmSlug()
    {
        var h=new Harness();h.Source.Setup(x=>x.GetRaidRecapReportsAsync("Other","new-realm","eu")).ReturnsAsync(Array.Empty<NinjaBotCore.Models.Wow.WclV2Report>());
        await h.Module.StartAsync(guild:"New Realm, Other, eu");
        h.Source.Verify(x=>x.GetRaidRecapReportsAsync("Other","new-realm","eu"),Times.Once);
        h.Discord.Verify(x=>x.GuildAsync(It.IsAny<IInteractionContext>()),Times.Never);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReportSelectionKeepsPickerAndShowsRecoveryGuidance(bool returnToReports)
    {
        var h=new Harness();
        h.Source.Setup(x=>x.GetRaidRecapReportsAsync("Guild","realm","us")).ReturnsAsync(new[]
        {
            new NinjaBotCore.Models.Wow.WclV2Report { Code="AbCdEfGh12345678",Title="Available raid",StartTime=2 },
            new NinjaBotCore.Models.Wow.WclV2Report { Code="ZbCdEfGh12345678",Title="Unavailable raid",StartTime=1 }
        });
        h.Source.Setup(x=>x.GetRaidRecapReportAsync("AbCdEfGh12345678")).ReturnsAsync(RaidRecapPanelTests.Report());
        h.Source.Setup(x=>x.GetRaidRecapReportAsync("ZbCdEfGh12345678")).ThrowsAsync(new InvalidOperationException("Detail request denied"));
        await h.Module.StartAsync(guild:"realm, Guild, us");
        SelectMenuComponent Picker()=>Assert.Single(RaidRecapPanelTests.Flatten(h.Edited.Components.Value.Components).OfType<SelectMenuComponent>());
        var choices=Picker().Options.Select(o=>(o.Label,o.Value)).ToArray();
        if(returnToReports)
        {
            var pick=Picker().CustomId.Split('~');
            await h.Module.SelectAsync(pick[1],pick[2],pick[3],new[]{"0"});
            Assert.Contains("Overview",RaidRecapPanelTests.Text(h.Edited.Components.Value));
            var reports=Assert.Single(RaidRecapPanelTests.Flatten(h.Edited.Components.Value.Components).OfType<ButtonComponent>(),b=>b.Label=="Reports").CustomId.Split('~');
            await h.Module.NavigateAsync(reports[1],reports[2],reports[3]);
        }
        var failed=Picker().CustomId.Split('~');
        await h.Module.SelectAsync(failed[1],failed[2],failed[3],new[]{"1"});
        Assert.Equal(choices,Picker().Options.Select(o=>(o.Label,o.Value)).ToArray());
        Assert.NotEqual(failed[2],Picker().CustomId.Split('~')[2]);
        Assert.Equal(MessageFlags.ComponentsV2,h.Edited.Flags.Value);
        Assert.Same(AllowedMentions.None,h.Edited.AllowedMentions.Value);
        h.Interaction.Verify(x=>x.DeferAsync(true,It.IsAny<RequestOptions>()),Times.Once);
        h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
        h.Source.Verify(x=>x.GetRaidRecapReportAsync("ZbCdEfGh12345678"),Times.Once);
        var text=RaidRecapPanelTests.Text(h.Edited.Components.Value);
        Assert.Contains(@"Warcraft Logs is unavailable, access was denied, or the report changed\.",text);
        Assert.Contains(@"reopen /raid\-recap",text);
        Assert.Contains("open the report on Warcraft Logs",text);
    }

    [Fact]
    public async Task ShareIsExplicitAndAmbiguousFailureIsNeverRetried()
    {
        var h=new Harness();var s=h.Sessions.Create(1,2,3);s.Report=RaidRecapPanelTests.Report();
        var sends=0;h.Discord.Setup(x=>x.PublishAsync(h.Context.Object,It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>())).Returns(()=>{sends++;return Task.FromException<ulong>(new TimeoutException());});
        await h.Module.NavigateAsync(s.Token,"0","share");
        Assert.True(s.ShareAttempted);Assert.Equal(1,sends);
        await h.Module.NavigateAsync(s.Token,"1","share");Assert.Equal(1,sends);
        Assert.Contains("check the channel",RaidRecapPanelTests.Text(h.Edited.Components.Value));
    }
    [Fact]
    public async Task RevokedSharePermissionCannotPublish()
    {
        var h=new Harness();var s=h.Sessions.Create(1,2,3);s.Report=RaidRecapPanelTests.Report();s.CanShare=true;
        h.Discord.Setup(x=>x.AccessAsync(h.Context.Object)).ReturnsAsync(new RaidRecapAccess(true,false));
        await h.Module.NavigateAsync(s.Token,"0","share");
        Assert.False(s.ShareAttempted);
        h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
    }
    [Fact]
    public async Task WrongActorCannotTouchProviderOrOriginResponse()
    {
        var h=new Harness();var s=h.Sessions.Create(9,2,3);s.Report=RaidRecapPanelTests.Report();
        await h.Module.NavigateAsync(s.Token,"0","damage");
        Assert.Null(h.Edited);h.Source.VerifyNoOtherCalls();h.Discord.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task ExpiryWhileFetchingPreventsRendering()
    {
        var now=DateTimeOffset.UnixEpoch;var h=new Harness(()=>now);
        h.Source.Setup(x=>x.GetRaidRecapReportAsync("AbCdEfGh12345678")).Returns(()=> { now+=TimeSpan.FromMinutes(11);return Task.FromResult(RaidRecapPanelTests.Report()); });
        await h.Module.StartAsync(report:"AbCdEfGh12345678");
        Assert.Null(h.Edited);
    }

    [Fact]
    public async Task ExpiryDuringPermissionLookupPreventsShare()
    {
        var now=DateTimeOffset.UnixEpoch;var h=new Harness(()=>now);var s=h.Sessions.Create(1,2,3);s.Report=RaidRecapPanelTests.Report();
        h.Discord.Setup(x=>x.AccessAsync(h.Context.Object)).Returns(()=>{now+=TimeSpan.FromMinutes(11);return Task.FromResult(new RaidRecapAccess(true,true));});
        await h.Module.NavigateAsync(s.Token,"0","share");
        Assert.False(s.ShareAttempted);h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
    }

    [Fact]
    public async Task PublicationRequiresNonOptionalNonNullCurrentSessionCapability()
    {
        foreach(var type in new[]{typeof(IRaidRecapDiscord),typeof(RaidRecapDiscord)})
        {
            var parameters=type.GetMethod(nameof(IRaidRecapDiscord.PublishAsync)).GetParameters();
            Assert.Equal(3,parameters.Length);
            Assert.Equal(typeof(Func<bool>),parameters[2].ParameterType);
            Assert.False(parameters[2].IsOptional);
        }
        var h=new Harness();h.Deferred=true;
        var adapter=new RaidRecapDiscord(Mock.Of<IServiceScopeFactory>(MockBehavior.Strict),ctx=>h.Discord.Object.AccessAsync(ctx));
        var method=typeof(RaidRecapDiscord).GetMethod(nameof(RaidRecapDiscord.PublishAsync));
        await Assert.ThrowsAsync<ArgumentNullException>(()=>(Task<ulong>)method.Invoke(adapter,new object[]{h.Context.Object,RaidRecapView.Build(RaidRecapPanelTests.Session()),null}));
        h.Discord.VerifyNoOtherCalls();
        Assert.Empty(h.Channel.Invocations);
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("eviction")]
    [InlineData("current")]
    [InlineData("revoked")]
    public async Task ConcretePublicationRechecksAuthorityAfterFinalPermissionAwait(string outcome)
    {
        var now=DateTimeOffset.UnixEpoch;
        var h=new Harness(()=>now,concretePublication:true,capacity:1);
        var s=h.Sessions.Create(1,2,3);s.Report=RaidRecapPanelTests.Report();
        var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permission=new TaskCompletionSource<RaidRecapAccess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookups=0;
        h.Discord.Setup(x=>x.AccessAsync(h.Context.Object)).Returns(()=>
        {
            Assert.True(h.Deferred);
            Assert.Equal(s.Actor,h.Context.Object.User.Id);
            Assert.Equal(s.Guild,h.Context.Object.Guild.Id);
            Assert.Equal(s.Channel,h.Context.Object.Channel.Id);
            if(++lookups==2) { waiting.SetResult();return permission.Task; }
            return Task.FromResult(new RaidRecapAccess(true,true));
        });
        var sharing=h.Module.NavigateAsync(s.Token,"0","share");
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(sharing.IsCompleted);
        Assert.True(s.ShareAttempted);
        Assert.True(h.Sessions.IsCurrent(s));
        Assert.DoesNotContain(h.Channel.Invocations,i=>i.Method.Name=="SendMessageAsync");
        if(outcome=="expiry") now=s.Expires;
        if(outcome=="eviction") h.Sessions.Create(4,2,3);
        permission.SetResult(new RaidRecapAccess(true,outcome!="revoked"));
        await sharing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(s.ShareAttempted);
        if(outcome is "expiry" or "eviction") Assert.False(h.Sessions.IsCurrent(s));
        var sends=h.Channel.Invocations.Where(i=>i.Method.Name=="SendMessageAsync").ToArray();
        if(outcome=="current")
        {
            var send=Assert.Single(sends);
            var args=send.Method.GetParameters().Select((p,i)=>(p.Name,Value:send.Arguments[i])).ToDictionary(p=>p.Name,p=>p.Value);
            Assert.Equal(MessageFlags.ComponentsV2,args["flags"]);
            Assert.Same(AllowedMentions.None,args["allowedMentions"]);
            var options=Assert.IsType<RequestOptions>(args["options"]);
            Assert.Equal(RetryMode.AlwaysFail,options.RetryMode);Assert.Equal(15000,options.Timeout);
            var panel=Assert.IsType<MessageComponent>(args["components"]);
            Assert.Empty(RaidRecapPanelTests.Flatten(panel.Components).OfType<SelectMenuComponent>());
            Assert.DoesNotContain(RaidRecapPanelTests.Flatten(panel.Components).OfType<ButtonComponent>(),b=>b.Style!=ButtonStyle.Link);
            Assert.DoesNotContain(s.Token,Newtonsoft.Json.JsonConvert.SerializeObject(panel));
            Assert.Contains("Overview shared in this channel",RaidRecapPanelTests.Text(h.Edited.Components.Value));
        }
        else Assert.Empty(sends);
        await h.Module.NavigateAsync(s.Token,s.Generation.ToString(),"share");
        Assert.Equal(sends.Length,h.Channel.Invocations.Count(i=>i.Method.Name=="SendMessageAsync"));
        h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
    }

    [Fact]
    public async Task AccessLookupFailureCannotRenderProtectedReport()
    {
        var h=new Harness();var s=h.Sessions.Create(1,2,3);s.Report=RaidRecapPanelTests.Report();
        h.Discord.Setup(x=>x.AccessAsync(h.Context.Object)).ThrowsAsync(new TimeoutException());
        await h.Module.NavigateAsync(s.Token,"0","overview");
        Assert.Null(h.Edited);
    }

    [Fact]
    public async Task ConcreteModuleRegistersSlashAndRoutesEveryGeneratedControl()
    {
        using var client=new DiscordRestClient();using var service=new InteractionService(client);
        using var deps=new ServiceCollection()
            .AddSingleton(new RaidRecapService(Mock.Of<IRaidRecapSource>(),new RaidRecapCache()))
            .AddSingleton(new RaidRecapSessions()).AddSingleton(Mock.Of<IRaidRecapDiscord>())
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<RaidRecapCommands>>(NullLogger<RaidRecapCommands>.Instance).BuildServiceProvider();
        await service.AddModuleAsync<RaidRecapCommands>(deps);
        Assert.Contains(service.SlashCommands,c=>c.Name=="raid-recap");
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPanelTests.Report(true,26);
        foreach(var view in new[]{"overview","bosses","damage","healing"})
        {
            s.View=view;
            foreach(var component in RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components))
            {
                var id=component switch { ButtonComponent b when b.Style!=ButtonStyle.Link=>b.CustomId,SelectMenuComponent m=>m.CustomId,_=>null };
                if(id==null)continue;
                var data=new Mock<IComponentInteractionData>();data.SetupGet(x=>x.CustomId).Returns(id);
                var interaction=new Mock<IComponentInteraction>();interaction.SetupGet(x=>x.Data).Returns(data.Object);
                Assert.True(service.SearchComponentCommand(interaction.Object).IsSuccess,id);
            }
        }
    }
    [Fact]
    public void PermissionPolicyRequiresOriginVisibilityAndBothSendRights()
    {
        Assert.Equal(new RaidRecapAccess(false,false),RaidRecapDiscord.Permissions(false,true,true,true));
        Assert.Equal(new RaidRecapAccess(true,false),RaidRecapDiscord.Permissions(true,true,false,true));
        Assert.Equal(new RaidRecapAccess(true,false),RaidRecapDiscord.Permissions(true,true,true,false));
        Assert.Equal(new RaidRecapAccess(true,true),RaidRecapDiscord.Permissions(true,true,true,true));
    }
}
