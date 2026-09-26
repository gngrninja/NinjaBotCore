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
        Assert.Contains("[Fight 1]",Text(s));
        Assert.True(Text(s).IndexOf("[Fight 1]",StringComparison.Ordinal)<Text(s).IndexOf("[Fight 10]",StringComparison.Ordinal));
        Assert.DoesNotContain("[Fight 11]",Text(s));
        await service.ApplyAsync(s,"attempts_next",null);
        Assert.Contains("[Fight 11]",Text(s)); Assert.Contains("[Fight 20]",Text(s));
        await service.ApplyAsync(s,"attempts_next",null);
        Assert.Contains("[Fight 23]",Text(s));
        await service.ApplyAsync(s,"attempts_prev",null);
        Assert.Contains("[Fight 11]",Text(s));
        source.VerifyNoOtherCalls();
    }

    [Fact]
    public void ProgressStatisticsDescribeKnownCompletedDurationsWithoutFilteringShortWipes()
    {
        var s=Session(Wipe(1,10000,5),Wipe(2,20000,20),Wipe(3,60000,null),Wipe(4,null,30),
            Wipe(5,999000,1) with { InProgress=true }, Wipe(6,30000) with { Kill=true });
        var text=Text(s);
        Assert.Contains("Completed combat time: **00:02:00** (4/5 known durations)",text);
        Assert.Contains("Median completed-wipe duration: **00:00:20** (n=3/4)",text);
        Assert.Contains("Best wipe active boss health: **5%**",text);
        Assert.Contains("Latest wipe active boss health: **30%**",text);
        Assert.DoesNotContain("999",text);
    }

    [Fact]
    public void MissingLatestWipeHealthDoesNotSilentlyFallBackToEarlierWipe()
    {
        var s=Session(Wipe(2,20000,null),Wipe(1,10000,5));
        Assert.Contains("Latest wipe active boss health: **Unknown**",Text(s));
        Assert.Contains("Median completed-wipe duration: **00:00:15** (n=2/2)",Text(s));
    }

    [Fact]
    public void OverviewOffersLinkedFirstDeathReviewForUnresolvedBoss()
    {
        var s=Session(Wipe(1));s.View="overview";
        Assert.Contains("Review first deaths",Text(s));
        Assert.Contains("#fight=1&type=deaths",Text(s));
        Assert.Contains("Analysis",Text(s));
    }

    [Fact]
    public void HealingLabelDoesNotClaimUnverifiedEffectiveHps()
    {
        var s=Session(Wipe(1) with { Kill=true });s.View="healing";
        Assert.Contains("WCL Healing table total / elapsed seconds",Text(s));
        Assert.Contains("not validated as effective healing",Text(s));
        Assert.Contains("not a quality grade",Text(s));
        Assert.DoesNotContain("Effective healing / elapsed",Text(s));
    }
}
