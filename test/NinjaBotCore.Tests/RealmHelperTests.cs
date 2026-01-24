using NinjaBotCore.Common;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class RealmHelperTests
    {
        [Theory]
        [InlineData("Sisters of Elune", "sisters-of-elune")]
        [InlineData("Area 52", "area-52")]
        [InlineData("Bleeding Hollow", "bleeding-hollow")]
        public void ToSlug_WithMultiWordRealm_ProducesCorrectSlug(string realmName, string expected)
        {
            var result = RealmHelper.ToSlug(realmName);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("Kul'Tiras", "kultiras")]
        [InlineData("Mal'Ganis", "malganis")]
        [InlineData("Quel'Thalas", "quelthalas")]
        public void ToSlug_WithApostrophe_RemovesApostrophe(string realmName, string expected)
        {
            var result = RealmHelper.ToSlug(realmName);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("Area 52", "area-52")]
        [InlineData("Realm123", "realm123")]
        public void ToSlug_WithNumbers_PreservesNumbers(string realmName, string expected)
        {
            var result = RealmHelper.ToSlug(realmName);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        public void ToSlug_WithNullOrEmpty_ReturnsEmpty(string realmName, string expected)
        {
            var result = RealmHelper.ToSlug(realmName);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("STORMRAGE", "stormrage")]
        [InlineData("StormRage", "stormrage")]
        [InlineData("stormrage", "stormrage")]
        public void ToSlug_WithMixedCase_ConvertsToLowercase(string realmName, string expected)
        {
            var result = RealmHelper.ToSlug(realmName);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void ToSlug_WithComplexRealm_HandlesAllTransformations()
        {
            // Realm with spaces, apostrophe, and mixed case
            var result = RealmHelper.ToSlug("Kil'jaeden US");
            Assert.Equal("kiljaeden-us", result);
        }
    }
}
