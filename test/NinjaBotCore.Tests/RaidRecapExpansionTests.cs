using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Moq;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Modules.Interactions.Wow;
using Xunit;

namespace NinjaBotCore.Tests;

public class RaidRecapExpansionTests
{
    // Synthetic metadata; no real provider/player data.
    private static RaidRecapSession Session(params RaidRecapFight[] fights)
    {
        var s=RaidRecapPanelTests.Session();
        s.Report=RaidRecapPanelTests.Report() with { Fights=fights }; s.View="bosses";
        return s;
    }
    private static RaidRecapFight Wipe(int id,double? duration=60000,double? health=10) =>
        new(id,10,4,"Progress",false,false,id*100000,id*100000+duration,health);
    private static string Text(RaidRecapSession s)=>RaidRecapPanelTests.Text(RaidRecapView.Build(s));

    [Fact]
    public async Task EveryAttemptIsReachableChronologicallyWithoutDetailFetches()
    {
        var s=Session(Enumerable.Range(1,23).Reverse().Select(i=>Wipe(i)).ToArray());
        var source=new Mock<IRaidRecapSource>(MockBehavior.Strict);
        var service=new RaidRecapService(source.Object,new RaidRecapCache());
        Assert.Contains("[#1]",Text(s));
        Assert.True(Text(s).IndexOf("[#1]",StringComparison.Ordinal)<Text(s).IndexOf("[#10]",StringComparison.Ordinal));
        Assert.DoesNotContain("[#11]",Text(s));
        await service.ApplyAsync(s,"attempts_next",null);
        Assert.Contains("[#11]",Text(s)); Assert.Contains("[#20]",Text(s));
        await service.ApplyAsync(s,"attempts_next",null);
        Assert.Contains("[#23]",Text(s));
        await service.ApplyAsync(s,"attempts_prev",null);
        Assert.Contains("[#11]",Text(s));
        source.VerifyNoOtherCalls();
    }

    [Fact]
    public void ProgressStatisticsDescribeKnownCompletedDurationsWithoutFilteringShortWipes()
    {
        var s=Session(Wipe(1,10000,5),Wipe(2,20000,20),Wipe(3,60000,null),Wipe(4,null,30),
            Wipe(5,999000,1) with { InProgress=true }, Wipe(6,30000) with { Kill=true });
        var text=Text(s);
        Assert.Contains("Time in combat **2:00** · 4 of 5 pulls timed",text);
        Assert.Contains("Median wipe **0:20** · 3 of 4 wipes timed",text);
        Assert.Contains("Best pull **5%**",text);
        Assert.Contains("Last wipe **30%**",text);
        Assert.DoesNotContain("999",text);
    }

    [Fact]
    public void MissingLatestWipeHealthDoesNotSilentlyFallBackToEarlierWipe()
    {
        var s=Session(Wipe(2,20000,null),Wipe(1,10000,5));
        Assert.Contains("Last wipe **—**",Text(s));
        Assert.Contains("Median wipe **0:15**",Text(s));
    }

    [Fact]
    public void OverviewOffersLinkedFirstDeathReviewForUnresolvedBoss()
    {
        var s=Session(Wipe(1));s.View="overview";
        Assert.Contains("Who died on the last wipe?",Text(s));
        Assert.Contains("#fight=1&type=deaths",Text(s));
        Assert.Contains("Still progressing",Text(s));
    }

    [Fact]
    public void HealingLabelDoesNotClaimUnverifiedEffectiveHps()
    {
        var s=Session(Wipe(1) with { Kill=true });s.View="healing";
        Assert.Contains("💚 Healing",Text(s));
        Assert.DoesNotContain("Effective healing",Text(s));
        // The explanation lives behind "How to read" and still makes no effective-healing claim.
        s.OutputHelp=true;
        Assert.Contains("WarcraftLogs' healing total",Text(s));
        Assert.Contains("Overhealing is not added",Text(s));
        Assert.DoesNotContain("Effective healing",Text(s));
    }
}
