using System;
using NinjaBotCore.Common;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class DiscordDeliveryFailurePolicyTests
    {
        [Theory]
        [InlineData(50001)] // Missing Access
        [InlineData(50013)] // Missing Permissions
        [InlineData(10003)] // Unknown Channel
        public void IsExpectedConfigurationFailure_KnownDiscordCodes_ReturnsTrue(int code)
        {
            Assert.True(DiscordDeliveryFailurePolicy.IsExpectedConfigurationFailure(code));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(500)]
        [InlineData(10062)] // Unknown Interaction is unrelated to channel delivery
        public void IsExpectedConfigurationFailure_OtherCodes_ReturnsFalse(int? code)
        {
            Assert.False(DiscordDeliveryFailurePolicy.IsExpectedConfigurationFailure(code));
        }

        [Theory]
        [InlineData(0UL, "no configured channel and no usable default text channel")]
        [InlineData(123UL, "configured channel 123 is unavailable")]
        public void DescribeUnavailableChannel_ExplainsConfigurationState(ulong channelId, string expected)
        {
            Assert.Equal(expected, DeliveryChannelWarningFormatter.DescribeUnavailableChannel(channelId));
        }

        [Fact]
        public void ShouldLog_FirstOccurrenceForKey_ReturnsTrue()
        {
            var policy = new DiscordDeliveryFailurePolicy(TimeSpan.FromHours(6));
            var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

            Assert.True(policy.ShouldLog(123, 456, "50001", now));
        }

        [Fact]
        public void ShouldLog_RepeatedOccurrenceInsideInterval_ReturnsFalse()
        {
            var policy = new DiscordDeliveryFailurePolicy(TimeSpan.FromHours(6));
            var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

            Assert.True(policy.ShouldLog(123, 456, "50001", now));
            Assert.False(policy.ShouldLog(123, 456, "50001", now.AddHours(5)));
        }

        [Fact]
        public void ShouldLog_OccurrenceAtIntervalBoundary_ReturnsTrue()
        {
            var policy = new DiscordDeliveryFailurePolicy(TimeSpan.FromHours(6));
            var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

            Assert.True(policy.ShouldLog(123, 456, "50001", now));
            Assert.True(policy.ShouldLog(123, 456, "50001", now.AddHours(6)));
        }

        [Fact]
        public void ShouldLog_DifferentGuildChannelOrReason_AreIndependent()
        {
            var policy = new DiscordDeliveryFailurePolicy(TimeSpan.FromHours(6));
            var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

            Assert.True(policy.ShouldLog(123, 456, "50001", now));
            Assert.True(policy.ShouldLog(124, 456, "50001", now));
            Assert.True(policy.ShouldLog(123, 457, "50001", now));
            Assert.True(policy.ShouldLog(123, 456, "50013", now));
        }

        [Fact]
        public void ShouldLog_NewObservation_ImmediatelyBeforePruneBoundary_RetainsOlderKey()
        {
            var policy = new DiscordDeliveryFailurePolicy(TimeSpan.FromHours(6));
            var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

            Assert.True(policy.ShouldLog(123, 456, "50001", now));
            Assert.True(policy.ShouldLog(
                789,
                987,
                "50013",
                now.AddHours(12).AddTicks(-1)));

            Assert.Equal(2, policy.RetainedKeyCount);
        }

        [Fact]
        public void ShouldLog_NewObservation_AtPruneBoundary_RemovesOlderKey()
        {
            var policy = new DiscordDeliveryFailurePolicy(TimeSpan.FromHours(6));
            var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

            Assert.True(policy.ShouldLog(123, 456, "50001", now));
            Assert.True(policy.ShouldLog(789, 987, "50013", now.AddHours(12)));

            Assert.Equal(1, policy.RetainedKeyCount);
        }
    }
}
