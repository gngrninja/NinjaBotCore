using System;
using System.Linq;
using Discord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Services;
using Xunit;
namespace NinjaBotCore.Tests;
public class RaidRecapPolishTests
{
    [Fact]
    public void ReflectedHelpIncludesRecapAndExistingLogs()
    {
        using var help=new HelpContentProvider(NullLogger<HelpContentProvider>.Instance,new ConfigurationBuilder().Build());
        help.RegenerateHelpContent();
        var commands=help.GetHelpContent().Categories.SelectMany(c=>c.Commands).ToArray();
        Assert.Contains(commands,c=>c.Name=="raid-recap");
        Assert.Contains(commands,c=>c.Name=="logs");
        Assert.Contains(commands,c=>c.Name=="watchlogs");
    }
    [Fact]
    public void DependencyRegistrationUsesExistingClientAndStableSingletonState()
    {
        var client=RaidRecapTransportTests.Client(new RaidRecapTransportTests.Handler());
        using var services=new ServiceCollection().AddSingleton(client).AddRaidRecap().BuildServiceProvider();
        Assert.Same(client,services.GetRequiredService<IRaidRecapSource>());
        Assert.Same(services.GetRequiredService<RaidRecapSessions>(),services.GetRequiredService<RaidRecapSessions>());
        Assert.NotNull(services.GetRequiredService<RaidRecapService>());
    }
    [Fact]
    public void OverviewHasFactualSynopsisAndProgressionSentence()
    {
        var s=RaidRecapPanelTests.Session();s.Report=RaidRecapPanelTests.Report(false);
        var text=RaidRecapPanelTests.Text(RaidRecapView.Build(s));
        Assert.Contains("0 encounter/difficulty groups cleared across 1 attempts",text);
        Assert.Contains("Most-pulled unresolved",text);
        Assert.Contains("boss health",text);
    }
    [Theory]
    [InlineData(double.MaxValue,"Unknown")]
    [InlineData(double.NaN,"Unknown")]
    [InlineData(90000000,"1d 01:00:00")]
    public void DurationIsBoundedAndDoesNotWrapDays(double ms,string expected)=>Assert.Equal(expected,RaidRecapView.Duration(ms));
    [Fact]
    public void PickerDescribesZoneAndRecordedSpan()
    {
        var s=RaidRecapPanelTests.Session();s.View="reports";
        s.Reports=new[]{new NinjaBotCore.Models.Wow.WclV2Report {Code="AbCdEfGh12345678",Title="Raid",StartTime=1000,EndTime=61000,Zone=new NinjaBotCore.Models.Wow.WclV2Zone {Name="Test zone"}}};
        var option=Assert.Single(RaidRecapPanelTests.Flatten(RaidRecapView.Build(s).Components).OfType<SelectMenuComponent>()).Options.Single();
        Assert.Contains("Test zone",option.Description);Assert.Contains("00:01:00",option.Description);
    }
}
