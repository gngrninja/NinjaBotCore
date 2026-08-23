using System;
using System.Collections.Generic;
using System.Linq;
using NinjaBotCore.Database;

namespace NinjaBotCore.Modules.Interactions.Crafting
{
    public static class CraftTicketFilters
    {
        public static List<string> AvailableProfessions(IEnumerable<CraftTicket> tickets) =>
            (tickets ?? Array.Empty<CraftTicket>())
                .Select(ticket => ticket.Profession?.Trim())
                .Where(profession => !string.IsNullOrWhiteSpace(profession))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(profession => profession, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public static IEnumerable<CraftTicket> ByProfession(
            IEnumerable<CraftTicket> tickets,
            string profession)
        {
            var rows = tickets ?? Array.Empty<CraftTicket>();
            if (string.IsNullOrWhiteSpace(profession)
                || string.Equals(profession, "all", StringComparison.OrdinalIgnoreCase))
            {
                return rows;
            }

            return rows.Where(ticket =>
                string.Equals(
                    ticket.Profession?.Trim(),
                    profession.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }
    }
}
