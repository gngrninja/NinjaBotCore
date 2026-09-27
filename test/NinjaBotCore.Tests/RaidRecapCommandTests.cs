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
        public readonly Mock<IRaidRecapPlayerSource> Players;
        public readonly RaidRecapSessions Sessions;
        public readonly RaidRecapCommands Module;
        public bool Deferred;
        public MessageProperties Edited;
        public readonly Mock<IMessageChannel> Channel=new();
        public Harness(Func<DateTimeOffset> clock=null,bool concretePublication=false,int capacity=256)
        {
            Players=Source.As<IRaidRecapPlayerSource>();
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
        Assert.Contains("1 kill",RaidRecapPanelTests.Text(h.Edited.Components.Value));
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
            Assert.Contains("1 kill",RaidRecapPanelTests.Text(h.Edited.Components.Value));
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
        Assert.Contains(@"WarcraftLogs is unavailable, access was denied, or the report changed\.",text);
        Assert.Contains(@"reopen /raid\-recap",text);
        Assert.Contains("open the report on WarcraftLogs",text);
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
        foreach(var view in new[]{"overview","bosses","damage","healing","analysis"})
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalysisProviderWaitRetainsDeferralAndExpiryGuards(bool expire)
    {
        var now=DateTimeOffset.UnixEpoch;var h=new Harness(()=>now);var s=h.Sessions.Create(1,2,3);
        s.Report=RaidRecapPanelTests.Report(false);
        var pending=new TaskCompletionSource<RaidRecapAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Source.Setup(x=>x.GetRaidRecapAnalysisAsync(s.Report,s.Report.Fights[0],"deaths",It.IsAny<System.Threading.CancellationToken>()))
            .Returns(()=>{Assert.True(h.Deferred);return pending.Task;});
        var request=h.Module.NavigateAsync(s.Token,"0","analysis");Assert.False(request.IsCompleted);
        if(expire) now+=TimeSpan.FromMinutes(11);
        pending.SetResult(new RaidRecapAnalysis("deaths",true,null){Deaths=new[]{new RaidRecapDeath(1,"PrivateActor",1000,"Spell")}});
        await request;
        if(expire) Assert.Null(h.Edited);
        else
        {
            Assert.Contains("PrivateActor",RaidRecapPanelTests.Text(h.Edited.Components.Value));
            Assert.Equal(MessageFlags.ComponentsV2,h.Edited.Flags.Value);Assert.Same(AllowedMentions.None,h.Edited.AllowedMentions.Value);
        }
        h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
    }

    [Fact]
    public async Task AnalysisFailureKeepsSubviewAndPullPickerPrivate()
    {
        var h=new Harness();var s=h.Sessions.Create(1,2,3);s.Report=RaidRecapPanelTests.Report(false);
        await h.Module.NavigateAsync(s.Token,"0","analysis");
        var text=RaidRecapPanelTests.Text(h.Edited.Components.Value);
        Assert.Contains("## 🔬",text);Assert.Contains("unavailable",text);
        Assert.Single(RaidRecapPanelTests.Flatten(h.Edited.Components.Value.Components).OfType<SelectMenuComponent>());
        Assert.Equal(MessageFlags.ComponentsV2,h.Edited.Flags.Value);Assert.Equal("",h.Edited.Content.Value);Assert.Null(h.Edited.Embed.Value);
        Assert.Same(AllowedMentions.None,h.Edited.AllowedMentions.Value);
        h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
    }

    [Fact]
    public async Task ComparisonCommandErrorClearsPreviousPayloadAndKeepsBothPickers()
    {
        var h=new Harness();var s=h.Sessions.Create(1,2,3);
        s.Report=RaidRecapPanelTests.Report() with {Fights=new[]{RaidRecapReviewTests.Pull(1),RaidRecapReviewTests.Pull(2)}};
        h.Source.Setup(x=>x.GetRaidRecapAnalysisAsync(s.Report,It.IsAny<RaidRecapFight>(),"deaths",It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new RaidRecapAnalysis("deaths",true,null));
        await h.Module.NavigateAsync(s.Token,s.Generation.ToString(),"bosses");
        await h.Module.NavigateAsync(s.Token,s.Generation.ToString(),"compare");
        await h.Module.NavigateAsync(s.Token,s.Generation.ToString(),"compare_deaths");
        Assert.Contains("Whole pull",RaidRecapPanelTests.Text(h.Edited.Components.Value));
        await h.Module.SelectAsync(s.Token,s.Generation.ToString(),"compare_a",new[]{"999"});
        var text=RaidRecapPanelTests.Text(h.Edited.Components.Value);Assert.DoesNotContain("Whole pull",text);Assert.Contains("unavailable",text);
        Assert.Equal(2,RaidRecapPanelTests.Flatten(h.Edited.Components.Value.Components).OfType<SelectMenuComponent>().Count());
        Assert.Equal(MessageFlags.ComponentsV2,h.Edited.Flags.Value);Assert.Same(AllowedMentions.None,h.Edited.AllowedMentions.Value);
    }

    [Theory]
    [InlineData("current")] [InlineData("expired")] [InlineData("revoked")]
    public async Task ComparisonWaitKeepsDeferralCurrentnessAndAccessChecks(string outcome)
    {
        var now=DateTimeOffset.UnixEpoch;var h=new Harness(()=>now);var s=h.Sessions.Create(1,2,3);
        s.Report=RaidRecapPanelTests.Report() with {Fights=new[]{RaidRecapReviewTests.Pull(1),RaidRecapReviewTests.Pull(2)}};
        var pending=new TaskCompletionSource<RaidRecapAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Source.Setup(x=>x.GetRaidRecapAnalysisAsync(s.Report,It.IsAny<RaidRecapFight>(),"deaths",It.IsAny<System.Threading.CancellationToken>()))
            .Returns<RaidRecapReport,RaidRecapFight,string,System.Threading.CancellationToken>((r,f,m,t)=>{
                Assert.True(h.Deferred);return f.Id==1?Task.FromResult(new RaidRecapAnalysis("deaths",true,null)):pending.Task;});
        await h.Module.NavigateAsync(s.Token,s.Generation.ToString(),"bosses");await h.Module.NavigateAsync(s.Token,s.Generation.ToString(),"compare");h.Edited=null;
        var work=h.Module.NavigateAsync(s.Token,s.Generation.ToString(),"compare_deaths");Assert.False(work.IsCompleted);
        if(outcome=="expired") now+=TimeSpan.FromMinutes(11);
        if(outcome=="revoked") h.Discord.Setup(x=>x.AccessAsync(h.Context.Object)).ReturnsAsync(new RaidRecapAccess(false,false));
        pending.SetResult(new RaidRecapAnalysis("deaths",true,null));await work;
        if(outcome=="current") Assert.Contains("Whole pull",RaidRecapPanelTests.Text(h.Edited.Components.Value));
        else Assert.Null(h.Edited);
        h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
    }

    [Theory]
    [InlineData("current")] [InlineData("expired")] [InlineData("revoked")] [InlineData("failed")]
    public async Task MechanicWaitKeepsPrivateDeferralAndCannotLeakOldResultAfterError(string outcome)
    {
        var now=DateTimeOffset.UnixEpoch;var h=new Harness(()=>now);var s=h.Sessions.Create(1,2,3);
        s.Report=RaidRecapMechanicTransportTests.Report;s.View="analysis";s.PullIndex=0;s.AnalysisMetric=RaidRecapMechanicTransportTests.Spin;
        var old=new RaidRecapMechanic(s.Report.SnapshotKey,2,5000,65000,"complete",new[]{new RaidRecapMechanicEvent(1,"PRIVATE OLD",1000,0,42)});
        s.Analysis=new RaidRecapAnalysis(RaidRecapMechanicTransportTests.Spin,true,null){Mechanic=old};
        var pending=new TaskCompletionSource<RaidRecapAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Source.Setup(x=>x.GetRaidRecapAnalysisAsync(s.Report,It.IsAny<RaidRecapFight>(),RaidRecapMechanicTransportTests.Junk,It.IsAny<System.Threading.CancellationToken>()))
            .Returns(()=>{Assert.True(h.Deferred);Assert.Null(s.Analysis);return pending.Task;});
        var work=h.Module.NavigateAsync(s.Token,"0",RaidRecapMechanicTransportTests.Junk);Assert.False(work.IsCompleted);
        if(outcome=="expired")now+=TimeSpan.FromMinutes(11);
        if(outcome=="revoked")h.Discord.Setup(x=>x.AccessAsync(h.Context.Object)).ReturnsAsync(new RaidRecapAccess(false,false));
        if(outcome=="failed")pending.SetException(new InvalidOperationException("synthetic"));
        else pending.SetResult(new RaidRecapAnalysis(RaidRecapMechanicTransportTests.Junk,true,null){Mechanic=old with {Events=new[]{new RaidRecapMechanicEvent(2,"PRIVATE NEW",2000,0,42)}}});
        await work;
        if(outcome is "expired" or "revoked")Assert.Null(h.Edited);
        else
        {
            var text=RaidRecapPanelTests.Text(h.Edited.Components.Value);Assert.DoesNotContain("PRIVATE OLD",text);
            if(outcome=="failed"){Assert.DoesNotContain("PRIVATE NEW",text);Assert.Contains("This mechanic is unavailable",text);Assert.Null(s.Analysis);}
            else Assert.Contains("PRIVATE NEW",text);
            Assert.Equal(MessageFlags.ComponentsV2,h.Edited.Flags.Value);Assert.Same(AllowedMentions.None,h.Edited.AllowedMentions.Value);Assert.Equal("",h.Edited.Content.Value);Assert.Null(h.Edited.Embed.Value);
        }
        h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
    }

    [Fact]
    public async Task PlayerLensDefersAndHoldsGateThroughProviderAndFinalEditRejectingStaleQueuedActor()
    {
        var h=new Harness();var s=h.Sessions.Create(1,2,3);s.Report=RaidRecapPanelTests.Report();
        h.Players.Setup(x=>x.GetRaidRecapRosterAsync(s.Report,s.Report.Fights[0],It.IsAny<System.Threading.CancellationToken>()))
            .Returns(()=>{Assert.True(h.Deferred);return Task.FromResult(RaidRecapPlayerFlowTests.Roster(s.Report,2));});
        await h.Module.NavigateAsync(s.Token,s.Generation.ToString(),"players");
        await h.Module.SelectAsync(s.Token,s.Generation.ToString(),"player_pull",new[]{"1"});
        await h.Module.SelectAsync(s.Token,s.Generation.ToString(),"player",new[]{"1"});
        var provider=new TaskCompletionSource<RaidRecapAnalysis>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Source.Setup(x=>x.GetRaidRecapAnalysisAsync(s.Report,s.Report.Fights[0],"deaths",It.IsAny<System.Threading.CancellationToken>()))
            .Returns(()=>{Assert.True(h.Deferred);return provider.Task;});
        var editing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var edit=new TaskCompletionSource<IUserMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Interaction.Setup(x=>x.ModifyOriginalResponseAsync(It.IsAny<Action<MessageProperties>>(),It.IsAny<RequestOptions>()))
            .Callback<Action<MessageProperties>,RequestOptions>((act,_)=>{h.Edited=new();act(h.Edited);editing.SetResult();}).Returns(edit.Task);
        h.Edited=null;var old=s.Generation.ToString();var active=h.Module.SelectAsync(s.Token,old,"player_lens",new[]{"deaths"});
        var stale=h.Module.SelectAsync(s.Token,old,"player",new[]{"2"});Assert.False(stale.IsCompleted);Assert.Null(h.Edited);
        provider.SetResult(new RaidRecapAnalysis("deaths",true,null){Deaths=new[]{new RaidRecapDeath(1,"Private",1000,"Spell")}});
        await editing.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(active.IsCompleted);Assert.False(stale.IsCompleted);
        Assert.Equal(MessageFlags.ComponentsV2,h.Edited.Flags.Value);Assert.Same(AllowedMentions.None,h.Edited.AllowedMentions.Value);
        edit.SetResult(null);await Task.WhenAll(active,stale).WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(1,s.PlayerPanel.ActorId);
        h.Source.Verify(x=>x.GetRaidRecapAnalysisAsync(s.Report,s.Report.Fights[0],"deaths",It.IsAny<System.Threading.CancellationToken>()),Times.Once);
        h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
    }

    private static RaidRecapSession OutputSession(Harness h,ulong actor=1,ulong guild=2,ulong channel=3)
    {
        var s=h.Sessions.Create(actor,guild,channel);s.Report=RaidRecapPanelTests.Report(true,26);s.View="healing";
        s.Performance=Enumerable.Range(1,12).Select(i=>new RaidRecapStanding("SYNTHETIC source "+i,1000,100)).ToArray();
        s.RankPage=1;s.KillPage=1;s.Notice="Partial observations retained";return s;
    }
    [Fact]
    public async Task OutputHelpUsesGuardedCommandAndHoldsFinalEditGateWithoutProviderIo()
    {
        var h=new Harness();var s=OutputSession(h);var original=s.Performance;
        var publicBefore=Newtonsoft.Json.JsonConvert.SerializeObject(RaidRecapView.Build(s,true));
        var editing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var edit=new TaskCompletionSource<IUserMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Interaction.Setup(x=>x.ModifyOriginalResponseAsync(It.IsAny<Action<MessageProperties>>(),It.IsAny<RequestOptions>()))
            .Callback<Action<MessageProperties>,RequestOptions>((act,_)=>{h.Edited=new();act(h.Edited);editing.SetResult();}).Returns(edit.Task);
        var help=Assert.Single(RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<ButtonComponent>(),b=>b.Label=="How to read").CustomId.Split('~');
        var active=h.Module.NavigateAsync(help[1],help[2],help[3]);
        await editing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stale=h.Module.NavigateAsync(help[1],help[2],help[3]);
        Assert.False(active.IsCompleted);Assert.False(stale.IsCompleted);
        Assert.Contains("Parse dots:",RaidRecapPanelTests.Text(h.Edited.Components.Value));
        Assert.Equal(MessageFlags.ComponentsV2,h.Edited.Flags.Value);Assert.Same(AllowedMentions.None,h.Edited.AllowedMentions.Value);
        Assert.Equal("",h.Edited.Content.Value);Assert.Null(h.Edited.Embed.Value);
        Assert.Equal("Partial observations retained",s.Notice);Assert.Same(original,s.Performance);
        Assert.Equal(1,s.RankPage);Assert.Equal(1,s.KillPage);Assert.Equal(0,s.KillIndex);
        edit.SetResult(null);await Task.WhenAll(active,stale).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1,s.Generation);h.Interaction.Verify(x=>x.ModifyOriginalResponseAsync(It.IsAny<Action<MessageProperties>>(),It.IsAny<RequestOptions>()),Times.Once);
        h.Interaction.Setup(x=>x.ModifyOriginalResponseAsync(It.IsAny<Action<MessageProperties>>(),It.IsAny<RequestOptions>()))
            .Callback<Action<MessageProperties>,RequestOptions>((act,_)=>{h.Edited=new();act(h.Edited);}).ReturnsAsync((RestInteractionMessage)null);
        var hide=Assert.Single(RaidRecapPanelTests.Flatten(h.Edited.Components.Value.Components).OfType<ButtonComponent>(),b=>b.Label=="Hide help").CustomId.Split('~');
        await h.Module.NavigateAsync(hide[1],hide[2],hide[3]);
        Assert.DoesNotContain("Parse dots:",RaidRecapPanelTests.Text(h.Edited.Components.Value));Assert.Equal(2,s.Generation);
        Assert.Same(original,s.Performance);Assert.Equal(1,s.RankPage);Assert.Equal(1,s.KillPage);Assert.Equal("Partial observations retained",s.Notice);
        h.Source.VerifyNoOtherCalls();h.Players.VerifyNoOtherCalls();
        h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
        Assert.Equal(publicBefore,Newtonsoft.Json.JsonConvert.SerializeObject(RaidRecapView.Build(s,true)));
    }
    [Theory]
    [InlineData("actor")][InlineData("guild")][InlineData("channel")][InlineData("expired")][InlineData("revoked")]
    [InlineData("final-expired")][InlineData("final-revoked")]
    public async Task OutputHelpCannotBypassOwnershipExpiryOrFinalAccess(string state)
    {
        var now=DateTimeOffset.UnixEpoch;var h=new Harness(()=>now);
        var s=OutputSession(h,state=="actor"?9u:1u,state=="guild"?9u:2u,state=="channel"?9u:3u);var checks=0;
        if(state=="expired")now=s.Expires;
        h.Discord.Setup(x=>x.AccessAsync(h.Context.Object)).Returns(()=>
        {
            Assert.True(h.Deferred);checks++;
            if(state=="final-expired" && checks==2)now=s.Expires;
            return Task.FromResult(new RaidRecapAccess(state!="revoked" && !(state=="final-revoked" && checks==2),false));
        });
        await h.Module.NavigateAsync(s.Token,"0","output_help");Assert.Null(h.Edited);
        if(state.StartsWith("final-",StringComparison.Ordinal))Assert.Equal(2,checks);
        h.Source.VerifyNoOtherCalls();h.Players.VerifyNoOtherCalls();
        h.Discord.Verify(x=>x.PublishAsync(It.IsAny<IInteractionContext>(),It.IsAny<MessageComponent>(),It.IsAny<Func<bool>>()),Times.Never);
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
