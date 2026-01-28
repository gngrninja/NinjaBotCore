using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Common
{
    public interface IWarcraftLogs
    {
        // Static data methods
        Task<List<CharClasses>> GetCharClasses();
        Task<List<Zones>> GetZones();
        Task<List<Zones>> GetClassicZones();
        Task<List<Zones>> GetVanillaZones();

        // Guild reports
        Task<List<Reports>> GetReportsFromGuild(string guildName, string realm, string region, bool isList = false, bool flip = false);
        Task<List<Reports>> GetReportsFromGuild(string guildName, string realm, string locale, string region, bool isList = false, bool flip = false);
        Task<List<Reports>> GetReportsFromGuild(string guildName, string realm, string locale, string region, string realmSlug, bool isList = false, bool flip = false);
        Task<List<Reports>> GetReportsFromGuildClassic(string guildName, string realm, string region, bool isList = false, bool flip = false);
        Task<List<Reports>> GetReportsFromGuildVanilla(string guildName, string realm, string region, bool isList = false, bool flip = false);
        Task<List<Reports>> GetReportsFromUser(string userName);

        // Character data
        Task<List<CharParses>> GetParsesFromCharacterName(string charName, string realm, string region = "us");
        Task<List<LogCharRankings>> GetRankingFromCharName(string charName, string realm, string region = "us");
        Task<List<LogCharRankings>> GetRankingFromCharName(string charName, string realm, string zone, string region = "us");

        // Fight data
        Task<Fights> GetFights(string code);

        // Rankings
        Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounter(int encounterID, string realmName, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us");
        Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounter(int encounterID, string realmName, string partition, string realmSlug, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us");
        Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterSlug(int encounterID, string realmSlug, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us");
        Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterSlug(int encounterID, string realmSlug, string partition, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us");
        Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterGuild(int encounterID, string realmName, string guildName, string partition, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us");
        Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterGuild(int encounterID, string realmName, string guildName, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us");
        Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterGuildSlug(int encounterID, string realmSlug, string guildName, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us");
        Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounterGuildSlug(int encounterID, string realmSlug, string partition, string guildName, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us");
        Task<WarcraftlogRankings.RankingObject> GetRankingsByEncounter(int encounterID, string realmName, string partition, string page = "1", string metric = "dps", int difficulty = 4, string regionName = "us");

        // Utility
        DateTime ConvTimeToLocalTimezone(DateTime time, string timezone = "America/Los_Angeles");

        // Note: Timer methods removed - log monitoring now handled by NinjaBotHelpers service
    }
}
