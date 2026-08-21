using Discord;

namespace NinjaBotCore.Modules.Interactions.Polls
{
    public static class PollAuthorization
    {
        public static bool CanCreatePoll(ulong guildOwnerId, IGuildUser user, IGuildChannel channel)
        {
            if (user.Id == guildOwnerId)
            {
                return true;
            }

            var permissions = user.GetPermissions(channel);
            if (user.GuildPermissions.Administrator || permissions.ManageMessages)
            {
                return true;
            }

            if (channel is not ITextChannel || channel is IThreadChannel)
            {
                return false;
            }

            return permissions.ViewChannel
                && permissions.SendMessages
                && permissions.SendPolls;
        }
    }
}
