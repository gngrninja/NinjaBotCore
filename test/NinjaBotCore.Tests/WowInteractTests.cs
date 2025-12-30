using System.Reflection;
using Xunit;
using NinjaBotCore.Modules.Interactions.Wow;

namespace NinjaBotCore.Tests
{
    public class WowInteractTests
    {
        private static string InvokeProgressBar(long current, long total, int length = 10)
        {
            var method = typeof(WowInteract).GetMethod("GetProgressBar", BindingFlags.NonPublic | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { current, total, length });
        }

        [Fact]
        public void GetProgressBar_EmptyTotal_ReturnsEmptyString()
        {
            var result = InvokeProgressBar(0, 0);
            Assert.Equal(string.Empty, result);
        }

        [Fact]
        public void GetProgressBar_PartialFill_ProducesExpectedBar()
        {
            var result = InvokeProgressBar(5, 10, 10);
            Assert.Equal("[█████░░░░░]", result);
        }

        [Fact]
        public void GetProgressBar_FullFill_UsesAllSlots()
        {
            var result = InvokeProgressBar(3, 3, 5);
            Assert.Equal("[█████]", result);
        }
    }
}
