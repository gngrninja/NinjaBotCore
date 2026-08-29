namespace NinjaBotCore.Common
{
    internal static class DeliveryChannelWarningFormatter
    {
        public static string DescribeUnavailableChannel(ulong channelId)
        {
            return channelId == 0
                ? "no configured channel and no usable default text channel"
                : $"configured channel {channelId} is unavailable";
        }
    }
}
