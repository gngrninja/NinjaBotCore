using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Moq;
using NinjaBotCore.Modules.Interactions.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using NinjaBotCore.Modules.Wow;
using Xunit;

namespace NinjaBotCore.Tests;

// The raid recap follows the /char WarcraftLogs cards. All identities here are SYNTHETIC.
public class RaidRecapDesignTests
{
    private const string Icon = "https://cdn.discordapp.com/icons/1/synthetic.png";

    private static RaidRecapSession Branded(RaidRecapSession s)
    {
        s.GuildName = "Synthetic **Guild** @everyone";
        s.GuildRegion = "us";
        s.GuildIconUrl = Icon;
        return s;
    }

    private static IMessageComponent[] Parts(RaidRecapSession s, bool shared = false) =>
        RaidRecapPanelTests.Flatten(RaidRecapView.Build(s, shared).Components).ToArray();

    private static string Text(RaidRecapSession s, bool shared = false) =>
        RaidRecapPanelTests.Text(RaidRecapView.Build(s, shared));

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(999d, "999")]
    [InlineData(1000d, "1.0K")]
    [InlineData(45678d, "45.7K")]
    [InlineData(1234567d, "1.23M")]
    [InlineData(2500000000d, "2.50B")]
    [InlineData(-1d, "—")]
    [InlineData(double.NaN, "—")]
    [InlineData(double.PositiveInfinity, "—")]
    public void NumbersAreCompactLikeCharLogs(double value, string expected) =>
        Assert.Equal(expected, RaidRecapFormat.Compact(value));

    [Theory]
    [InlineData(1000d, "0:01")]
    [InlineData(332000d, "5:32")]
    [InlineData(3932000d, "1:05:32")]
    [InlineData(0d, "—")]
    [InlineData(-5d, "—")]
    [InlineData(double.NaN, "—")]
    public void DurationsAreMinutesAndSeconds(double ms, string expected) =>
        Assert.Equal(expected, RaidRecapFormat.Clock(ms));

    [Fact]
    public void ElapsedAllowsZeroAndSpanReadsAsARaidNight()
    {
        Assert.Equal("0:00", RaidRecapFormat.Elapsed(0));
        Assert.Equal("—", RaidRecapFormat.Elapsed(-1));
        Assert.Equal("3h 12m", RaidRecapFormat.Span(11520000));
        Assert.Equal("45m", RaidRecapFormat.Span(2700000));
        Assert.Equal("—", RaidRecapFormat.Span(null));
        Assert.Equal("Death Knight", RaidRecapFormat.ClassName("DeathKnight"));
        Assert.Equal("Beast Mastery", RaidRecapFormat.ClassName("BeastMastery"));
        Assert.Equal(new[] { "1st", "2nd", "3rd", "4th", "11th", "12th", "13th", "21st", "102nd" },
            new[] { 1, 2, 3, 4, 11, 12, 13, 21, 102 }.Select(RaidRecapFormat.Ordinal));
    }

    [Theory]
    [InlineData(0, "⚪")]
    [InlineData(24.9, "⚪")]
    [InlineData(25, "🟢")]
    [InlineData(50, "🔵")]
    [InlineData(75, "🟣")]
    [InlineData(95, "🟠")]
    [InlineData(99, "🩷")]
    [InlineData(99.9, "🩷")]
    [InlineData(100, "🟡")]
    public void CharAndRaidRecapShareOneParsePalette(double percentile, string emoji)
    {
        var badge = RaidRecapParsePalette.Badge(percentile);
        Assert.Equal(emoji, badge.Emoji);
        Assert.Equal(emoji, CharViewHelpers.GetParseEmoji(percentile));
        Assert.Equal(new Color(badge.Color), CharViewHelpers.GetParseColor(percentile));
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(250)]
    [InlineData(double.NaN)]
    public void CharParseHelpersStayInsideThePaletteForBadInput(double percentile)
    {
        Assert.Contains(CharViewHelpers.GetParseEmoji(percentile), new[] { "⚪", "🟡" });
        Assert.NotEqual(default, CharViewHelpers.GetParseColor(percentile));
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("bosses")]
    [InlineData("damage")]
    [InlineData("healing")]
    [InlineData("analysis")]
    public void HeaderNamesTheGuildShowsTheServerIconAndMarksTheActiveView(string view)
    {
        var s = Branded(RaidRecapOutputTests.Sample(view));
        var payload = RaidRecapView.Build(s);
        var header = payload.Components.OfType<ContainerComponent>().First();
        var section = Assert.IsType<SectionComponent>(header.Components.First());
        Assert.IsType<ThumbnailComponent>(section.Accessory);
        var title = Assert.Single(section.Components.OfType<TextDisplayComponent>()).Content;
        Assert.StartsWith("# 📊 Raid Recap · Synthetic", title);
        Assert.DoesNotContain("@everyone", title);
        Assert.Contains("Heroic", title);

        var tabs = RaidRecapPanelTests.Flatten(payload.Components).OfType<ActionRowComponent>().First()
            .Components.Cast<ButtonComponent>().ToArray();
        Assert.All(tabs, tab => Assert.NotNull(tab.Emote));
        Assert.Equal(ButtonStyle.Success, Assert.Single(tabs, tab => tab.CustomId.EndsWith("~" + view)).Style);
        Assert.All(tabs.Where(tab => !tab.CustomId.EndsWith("~" + view)), tab => Assert.Equal(ButtonStyle.Primary, tab.Style));

        Assert.Contains("| Data from WarcraftLogs · fetched <t:", Text(s));
        Assert.Contains("(US)", Text(s));
        RaidRecapPlayerReachabilityTests.Check(s);
    }

    [Theory]
    [InlineData("http://cdn.discordapp.com/icons/1/plain.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void OnlyHttpsIconsBecomeThumbnails(string icon)
    {
        var s = RaidRecapOutputTests.Sample("overview");
        s.GuildIconUrl = icon;
        Assert.Empty(Parts(s).OfType<ThumbnailComponent>());
        Assert.StartsWith("# 📊 Raid Recap", Text(s));
    }

    [Theory]
    [InlineData("damage")]
    [InlineData("healing")]
    public async Task LargestDamageCardStaysInsideDiscordLimitsWithIconAndHelpOpen(string view)
    {
        var s = Branded(RaidRecapOutputTests.Sample(view, 51));
        var service = new RaidRecapService(Mock.Of<IRaidRecapSource>(MockBehavior.Strict), new RaidRecapCache());
        await service.ApplyAsync(s, "output_help", null);
        Assert.Equal(5, RaidRecapOutputTests.Cards(s).Length);
        Assert.InRange(Parts(s).Length, 1, 40);
        RaidRecapPlayerReachabilityTests.Check(s);
    }

    [Theory]
    [InlineData("mechanics")]
    [InlineData("compare")]
    [InlineData("first-deaths")]
    [InlineData("player")]
    public void BusiestCardsStayInsideDiscordLimitsWithIconAndHelpOpen(string card)
    {
        var s = Branded(RaidRecapPanelTests.Session());
        var hostile = string.Concat(Enumerable.Repeat("😀**@everyone[]\\\n", 200));
        s.Report = RaidRecapPanelTests.Report(false, 1) with
        {
            Title = hostile,
            Fights = Enumerable.Range(1, 60)
                .Select(i => new RaidRecapFight(i, 3497, 4, hostile, false, false, i * 100000, i * 100000 + 60000, 12.5))
                .ToArray()
        };
        s.OutputHelp = true;
        s.CanShare = true;
        var fight = s.Report.CompletedPulls[0];
        var deaths = new RaidRecapAnalysis("deaths", true, null)
        {
            Deaths = Enumerable.Range(1, 40).Select(i => new RaidRecapDeath(i, hostile, 1000, hostile)).ToArray()
        };

        if (card == "mechanics")
        {
            s.View = "analysis";
            s.PullIndex = 0;
            s.AnalysisMetric = RaidRecapMechanics.Junk;
            s.Analysis = new RaidRecapAnalysis(RaidRecapMechanics.Junk, false, hostile)
            {
                Mechanic = new(s.Report.SnapshotKey, fight.Id, fight.StartMs.Value, fight.EndMs.Value, "partial",
                    Enumerable.Range(1, 40).Select(i => new RaidRecapMechanicEvent(i, hostile, i * 1000, 1, 0)).ToArray())
            };
        }
        else if (card is "compare" or "first-deaths")
        {
            s.View = "bosses";
            s.Comparing = true;
            s.CompareSnapshotKey = s.Report.SnapshotKey;
            s.CompareAIndex = 0;
            s.CompareBIndex = 1;
            var candidates = RaidRecapReview.Candidates(s.Report.Bosses[0]);
            s.Comparison = new(s.Report.SnapshotKey, candidates[0], candidates[1], deaths, deaths);
            s.CompareLosses = card == "first-deaths";
        }
        else
        {
            s.PlayerPanel = new()
            {
                SnapshotKey = s.Report.SnapshotKey,
                Fight = fight,
                Roster = new(s.Report.SnapshotKey, fight.Id,
                    Enumerable.Range(1, 60).Select(i => new RaidRecapPlayer(i, hostile, "DeathKnight", "Unholy", "dps", "Realm", "US", true)).ToArray(),
                    false),
                ActorId = 1,
                Lens = "deaths"
            };
            s.PlayerPanel.Observations["deaths"] = deaths with { Complete = false, Notice = hostile };
            s.Notice = hostile;
        }

        Assert.Single(Parts(s).OfType<ThumbnailComponent>());
        Assert.Contains("**❔ How to read**", Text(s));
        RaidRecapPlayerReachabilityTests.Check(s);
    }

    [Fact]
    public void SelectMenusShowPlainNamesWithoutMarkdownEscapes()
    {
        var s = RaidRecapOutputTests.Sample("bosses");
        s.Report = s.Report with
        {
            Fights = s.Report.Fights.Select(f => f with { Name = "Nexus-King (Test) @everyone" }).ToArray()
        };
        foreach (var view in new[] { "bosses", "damage", "analysis" })
        {
            s.View = view;
            s.PullIndex = 0;
            var options = Parts(s).OfType<SelectMenuComponent>().SelectMany(menu => menu.Options).ToArray();
            Assert.NotEmpty(options);
            Assert.All(options, option =>
            {
                Assert.Contains("Nexus-King (Test)", option.Label);
                Assert.DoesNotContain("\\", option.Label);
                Assert.DoesNotContain("@everyone", option.Label);
                Assert.NotNull(option.Emote);
            });
        }

        // Card text is markdown, so the same name is escaped there.
        s.View = "bosses";
        Assert.Contains("Nexus\\-King", Text(s));
    }

    [Fact]
    public void SharedCardUsesTheSameHeaderAndFooterWithoutControlsOrNames()
    {
        var s = Branded(RaidRecapOutputTests.Sample("damage"));
        var parts = Parts(s, shared: true);
        var button = Assert.Single(parts.OfType<ButtonComponent>());
        Assert.Equal(ButtonStyle.Link, button.Style);
        Assert.Equal("WarcraftLogs", button.Label);
        Assert.Empty(parts.OfType<SelectMenuComponent>());
        Assert.Single(parts.OfType<ThumbnailComponent>());

        var text = Text(s, shared: true);
        Assert.StartsWith("# 📊 Raid Recap · Synthetic", text);
        Assert.Contains("✅", text);
        Assert.Contains("| Data from WarcraftLogs", text);
        Assert.DoesNotContain("Source1", text);
        Assert.DoesNotContain("How to read", text);
        Assert.DoesNotContain("Worth a look", text);
        Assert.DoesNotContain(s.Token, Newtonsoft.Json.JsonConvert.SerializeObject(RaidRecapView.Build(s, true)));
    }

    [Fact]
    public void DamageRowsUseMedalsCompactOutputAndParseDots()
    {
        var s = RaidRecapOutputTests.Sample("damage");
        var rows = RaidRecapPlayerPresentation.OutputPages(s).SelectMany(page => page).ToArray();
        Assert.StartsWith("1. 🥇 ", rows[0]);
        Assert.StartsWith("2. 🥈 ", rows[1]);
        Assert.StartsWith("3. 🥉 ", rows[2]);
        Assert.StartsWith("4. **", rows[3]);
        Assert.Contains("**10.0K** DPS", rows[0]);
        Assert.Contains("🟢 **48**", rows[0]);
        Assert.Contains("no parse", rows[4]);
        Assert.All(rows, row => Assert.Contains("\n-# 💚 Holy Priest", row));
    }
}
