using System.Reflection;
using Xunit;
using NinjaBotCore.Modules.Interactions.Wow;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for WowInteract utility methods
    /// </summary>
    public class WowInteractTests
    {
        private static string InvokeProgressBar(long current, long total, int length = 10)
        {
            var method = typeof(WowInteract).GetMethod("GetProgressBar", BindingFlags.NonPublic | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { current, total, length });
        }

        #region Progress Bar Tests

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

        [Fact]
        public void GetProgressBar_ZeroCurrent_AllEmpty()
        {
            var result = InvokeProgressBar(0, 10, 10);
            Assert.Equal("[░░░░░░░░░░]", result);
        }

        [Fact]
        public void GetProgressBar_OneThird_ThreeFilled()
        {
            var result = InvokeProgressBar(1, 3, 9);
            Assert.Equal("[███░░░░░░]", result);
        }

        [Fact]
        public void GetProgressBar_NegativeTotal_ThrowsException()
        {
            // Edge case: Negative total causes ArgumentOutOfRangeException
            // due to negative string repetition count
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => InvokeProgressBar(5, -10));
            Assert.IsType<System.ArgumentOutOfRangeException>(exception.InnerException);
        }

        [Fact]
        public void GetProgressBar_CurrentGreaterThanTotal_ThrowsException()
        {
            // Edge case: When current > total, the calculation produces negative count
            // for empty blocks, causing ArgumentOutOfRangeException
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(
                () => InvokeProgressBar(15, 10, 10));
            Assert.IsType<System.ArgumentOutOfRangeException>(exception.InnerException);
        }

        [Theory]
        [InlineData(0, 100, 10, "[░░░░░░░░░░]")]
        [InlineData(50, 100, 10, "[█████░░░░░]")]
        [InlineData(100, 100, 10, "[██████████]")]
        [InlineData(25, 100, 4, "[█░░░]")]
        [InlineData(75, 100, 4, "[███░]")]
        public void GetProgressBar_VariousPercentages_ProducesCorrectBar(long current, long total, int length, string expected)
        {
            var result = InvokeProgressBar(current, total, length);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void GetProgressBar_SmallLength_StillWorks()
        {
            var result = InvokeProgressBar(1, 2, 2);
            Assert.Equal("[█░]", result);
        }

        [Fact]
        public void GetProgressBar_LargeLength_ProducesLongBar()
        {
            var result = InvokeProgressBar(10, 20, 20);
            Assert.Equal("[██████████░░░░░░░░░░]", result);
        }

        #endregion
    }
}
