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
}
