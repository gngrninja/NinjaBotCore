using NinjaBotCore.Models.Wow.Forever;
using Xunit;

namespace NinjaBotCore.Tests;

// Forever names are first + surname, unique per region. Every name here is SYNTHETIC.
public class ForeverNamesTests
{
    [Theory]
    [InlineData("  asha ", "Asha")]
    [InlineData("Brightvale", "Brightvale")]
    [InlineData("d'arra", "D'arra")]
    [InlineData("Mira-Lee", "Mira-Lee")]
    [InlineData("zoë", "Zoë")]
    [InlineData("McAllister", "McAllister")]
    public void Valid_parts_are_trimmed_and_capitalised_but_otherwise_kept(string input, string expected)
    {
        Assert.True(ForeverNames.TryCleanPart(input, "First name", out var display, out var error), error);
        Assert.Equal(expected, display);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("A")]
    [InlineData("Abcdefghijklmnopqrstuvwxy")]
    [InlineData("Asha Brightvale")]
    [InlineData("Asha2")]
    [InlineData("'Asha")]
    [InlineData("As\nha")]
    [InlineData("**Asha**")]
    public void Invalid_parts_are_rejected_with_a_message(string input)
    {
        Assert.False(ForeverNames.TryCleanPart(input, "Surname", out _, out var error));
        Assert.StartsWith("Surname", error);
    }

    [Fact]
    public void Normalization_ignores_case_and_composition_but_not_accents()
    {
        Assert.Equal(ForeverNames.Normalize("Zoë"), ForeverNames.Normalize("ZOË"));
        Assert.Equal(ForeverNames.Normalize("asha"), ForeverNames.Normalize("ASHA"));
        Assert.NotEqual(ForeverNames.Normalize("Zoë"), ForeverNames.Normalize("Zoe"));
        Assert.NotEqual(ForeverNames.Normalize("Mira-Lee"), ForeverNames.Normalize("MiraLee"));
    }

    [Fact]
    public void Guild_names_allow_punctuation_but_not_control_characters_or_overlong_input()
    {
        Assert.True(ForeverNames.TryCleanGuildName("  <Ninjas & Co>  ", out var name, out _));
        Assert.Equal("<Ninjas & Co>", name);
        Assert.False(ForeverNames.TryCleanGuildName("Line\nbreak", out _, out _));
        Assert.False(ForeverNames.TryCleanGuildName("", out _, out _));
        Assert.False(ForeverNames.TryCleanGuildName(new string('x', 65), out _, out _));
    }

    [Theory]
    [InlineData("Asha", "Brightvale", "asha-brightvale")]
    [InlineData(" ASHA ", "brightvale", "asha-brightvale")]
    [InlineData("D'arra", "O’Dawn", "darra-odawn")]
    [InlineData("Mira-Lee", "Brightvale", "mira-lee-brightvale")]
    [InlineData("Zoë", "Élan", "zoë-élan")]
    public void A_character_slug_is_both_names_lowercased_without_apostrophes(string first, string last, string expected)
        => Assert.Equal(expected, ForeverNames.CharacterSlug(first, last));

    [Theory]
    [InlineData("Synthetic Guild", "synthetic-guild")]
    [InlineData("  Kael's   Chosen ", "kaels-chosen")]
    [InlineData("ONE", "one")]
    [InlineData(" ' ", "")]
    public void A_guild_slug_turns_spaces_into_single_hyphens(string name, string expected)
        => Assert.Equal(expected, ForeverNames.Slug(name));

    [Fact]
    public void Armory_links_are_per_region_and_escape_the_slug()
    {
        Assert.Equal("https://worldofwarcraft.blizzard.com/en-us/forever/eu/armory/character/asha-brightvale",
            ForeverNames.ArmoryUrl(ForeverRegion.EU, "asha-brightvale"));
        Assert.EndsWith("/forever/tw/armory/character/zo%C3%AB-%C3%A9lan", ForeverNames.ArmoryUrl(ForeverRegion.TW, "zoë-élan"));
    }

    [Theory]
    [InlineData("asha brightvale", "Asha", "Brightvale")]
    [InlineData("  Asha \t  Brightvale ", "Asha", "Brightvale")]
    [InlineData("d'arra mira-lee", "D'arra", "Mira-lee")]
    public void A_typed_full_name_splits_into_first_name_and_surname(string input, string first, string last)
    {
        Assert.True(ForeverNames.TrySplitFullName(input, out var gotFirst, out var gotLast, out var error), error);
        Assert.Equal((first, last), (gotFirst, gotLast));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Asha")]
    [InlineData("Asha of Brightvale")]
    [InlineData("Asha 2")]
    [InlineData("0")]
    public void Anything_but_two_valid_names_is_not_a_full_name(string input)
    {
        Assert.False(ForeverNames.TrySplitFullName(input, out _, out _, out var error));
        Assert.NotEmpty(error);
    }
}
