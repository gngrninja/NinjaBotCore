using System.Collections.Generic;
using System.Linq;
using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Crafting;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class CraftTicketFilterTests
    {
        [Fact]
        public void ProfessionFilterUsesPersistedTicketProfessionForFreeformAndRenamedItems()
        {
            var tickets = new List<CraftTicket>
            {
                new() { Id = 1, ItemName = "Resolved Item Name", Profession = "Blacksmithing" },
                new() { Id = 2, ItemName = "Freeform Request", Profession = "Alchemy" },
                new() { Id = 3, ItemName = "Unknown", Profession = null }
            };

            var professions = CraftTicketFilters.AvailableProfessions(tickets);
            var blacksmithing = CraftTicketFilters.ByProfession(tickets, "Blacksmithing").ToList();
            var all = CraftTicketFilters.ByProfession(tickets, "all").ToList();

            Assert.Equal(new[] { "Alchemy", "Blacksmithing" }, professions);
            Assert.Single(blacksmithing);
            Assert.Equal(1, blacksmithing[0].Id);
            Assert.Equal(3, all.Count);
        }

        [Fact]
        public void InitialThreadMentionsAreRestrictedToRequesterAndMappedRole()
        {
            var mentions = CraftTicketUpdater.BuildInitialThreadAllowedMentions(123, 456);

            Assert.Null(mentions.AllowedTypes);
            Assert.Equal(new ulong[] { 123 }, mentions.UserIds);
            Assert.Equal(new ulong[] { 456 }, mentions.RoleIds);
        }

        [Fact]
        public void ThreadNotificationsAllowOnlyIntentionalUserMentions()
        {
            var mentions = CraftTicketUpdater.BuildThreadNotificationAllowedMentions(123, 456);

            Assert.Null(mentions.AllowedTypes);
            Assert.Equal(new ulong[] { 123, 456 }, mentions.UserIds);
            Assert.Empty(mentions.RoleIds);
        }
    }
}
