using System.Linq;
using System.Reflection;
using Discord;
using Discord.Interactions;
using Moq;
using NinjaBotCore.Modules.Interactions.Polls;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class PollCommandPermissionTests
    {
        [Fact]
        public void PollCommands_DoesNotHideGroupBehindManageMessagesDefault()
        {
            var defaults = typeof(PollCommands)
                .GetCustomAttributes<DefaultMemberPermissionsAttribute>(inherit: true);

            Assert.Empty(defaults);
        }

        [Fact]
        public void PollCreate_DoesNotUseGuildWideManageMessagesPrecondition()
        {
            var method = typeof(PollCommands).GetMethod(nameof(PollCommands.PollCreate));

            Assert.NotNull(method);
            Assert.Empty(method!.GetCustomAttributes<RequireUserPermissionAttribute>(inherit: true));
        }

        [Fact]
        public void CanCreatePoll_AllowsEffectiveCreatePollsPermissionInTargetChannel()
        {
            var user = CreateGuildUser(42);
            var allowedChannel = new Mock<ITextChannel>();
            var deniedChannel = new Mock<ITextChannel>();
            user.Setup(value => value.GetPermissions(allowedChannel.Object))
                .Returns(new ChannelPermissions((ulong)(
                    GuildPermission.ViewChannel
                    | GuildPermission.SendMessages
                    | GuildPermission.SendPolls)));
            user.Setup(value => value.GetPermissions(deniedChannel.Object))
                .Returns(new ChannelPermissions(0));

            Assert.True(PollAuthorization.CanCreatePoll(1, user.Object, allowedChannel.Object));
            Assert.False(PollAuthorization.CanCreatePoll(1, user.Object, deniedChannel.Object));
        }

        [Fact]
        public void CanCreatePoll_DeniesCreatePollsWithoutSendMessages()
        {
            var user = CreateGuildUser(42);
            var channel = new Mock<ITextChannel>();
            user.Setup(value => value.GetPermissions(channel.Object))
                .Returns(new ChannelPermissions((ulong)(
                    GuildPermission.ViewChannel
                    | GuildPermission.SendPolls)));

            Assert.False(PollAuthorization.CanCreatePoll(1, user.Object, channel.Object));
        }

        [Fact]
        public void CanCreatePoll_DeniesCreatePollsWithoutViewChannel()
        {
            var user = CreateGuildUser(42);
            var channel = new Mock<ITextChannel>();
            user.Setup(value => value.GetPermissions(channel.Object))
                .Returns(new ChannelPermissions((ulong)(
                    GuildPermission.SendMessages
                    | GuildPermission.SendPolls)));

            Assert.False(PollAuthorization.CanCreatePoll(1, user.Object, channel.Object));
        }

        [Fact]
        public void CanCreatePoll_DeniesThreadRelayForOrdinaryMember()
        {
            var user = CreateGuildUser(42);
            var thread = new Mock<IThreadChannel>();
            user.Setup(value => value.GetPermissions(thread.Object))
                .Returns(new ChannelPermissions((ulong)(
                    GuildPermission.ViewChannel
                    | GuildPermission.SendMessages
                    | GuildPermission.SendMessagesInThreads
                    | GuildPermission.SendPolls)));

            Assert.False(PollAuthorization.CanCreatePoll(1, user.Object, thread.Object));
        }

        [Fact]
        public void CanCreatePoll_PreservesEffectiveManageMessagesPermission()
        {
            var user = CreateGuildUser(42);
            var channel = new Mock<IGuildChannel>();
            user.Setup(value => value.GetPermissions(channel.Object))
                .Returns(new ChannelPermissions((ulong)GuildPermission.ManageMessages));

            Assert.True(PollAuthorization.CanCreatePoll(1, user.Object, channel.Object));
        }

        [Fact]
        public void CanCreatePoll_AllowsGuildOwnerWithoutChannelBits()
        {
            var user = CreateGuildUser(42);
            var channel = new Mock<IGuildChannel>();
            user.Setup(value => value.GetPermissions(channel.Object))
                .Returns(new ChannelPermissions(0));

            Assert.True(PollAuthorization.CanCreatePoll(42, user.Object, channel.Object));
        }

        [Fact]
        public void CanCreatePoll_AllowsAdministratorWithoutChannelBits()
        {
            var user = CreateGuildUser(42);
            var channel = new Mock<IGuildChannel>();
            user.SetupGet(value => value.GuildPermissions)
                .Returns(new GuildPermissions((ulong)GuildPermission.Administrator));
            user.Setup(value => value.GetPermissions(channel.Object))
                .Returns(new ChannelPermissions(0));

            Assert.True(PollAuthorization.CanCreatePoll(1, user.Object, channel.Object));
        }

        [Fact]
        public void CanCreatePoll_UsesChannelOverrideInsteadOfGuildCreatePollsBit()
        {
            var user = CreateGuildUser(42);
            var channel = new Mock<IGuildChannel>();
            user.SetupGet(value => value.GuildPermissions)
                .Returns(new GuildPermissions((ulong)GuildPermission.SendPolls));
            user.Setup(value => value.GetPermissions(channel.Object))
                .Returns(new ChannelPermissions(0));

            Assert.False(PollAuthorization.CanCreatePoll(1, user.Object, channel.Object));
        }

        [Fact]
        public void CanCreatePoll_DeniesMemberWithoutEffectiveChannelPermission()
        {
            var user = CreateGuildUser(42);
            var channel = new Mock<IGuildChannel>();
            user.Setup(value => value.GetPermissions(channel.Object))
                .Returns(new ChannelPermissions(0));

            Assert.False(PollAuthorization.CanCreatePoll(1, user.Object, channel.Object));
        }

        private static Mock<IGuildUser> CreateGuildUser(ulong userId)
        {
            var user = new Mock<IGuildUser>();
            user.SetupGet(value => value.Id).Returns(userId);
            return user;
        }
    }
}
