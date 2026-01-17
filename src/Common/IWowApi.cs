using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Common
{
    public interface IWowApi
    {
        // Initialization
        Task GetWowDataAsync(CancellationToken cancellationToken = default);
        Task<bool> WaitForInitializationAsync(CancellationToken cancellationToken = default);

        // API Request methods
        Task<string> GetAPIRequestAsync(string url, string region = "us", CancellationToken cancellationToken = default);
        Task<string> GetAPIRequestAsync(string url, bool fullUrl, CancellationToken cancellationToken = default);
        Task<string> GetAPIRequestAsync(string url, string locale, string region = "us", CancellationToken cancellationToken = default);
        Task<string> GetWowToken(string username, string password);

        // Character methods
        Task<Character> GetCharInfoAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default);
        Task<ArmorySummary> GetArmorySummaryAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default);
        Task<ArmoryEquipment> GetArmoryEquipmentAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default);
        Task<ArmoryMedia> GetArmoryMediaAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default);
        Task<MountCollectionResponse> GetCharacterMountsAsync(string name, string realm, string regionName = "us", CancellationToken cancellationToken = default);
        Task<List<FoundChar>> SearchArmoryAsync(string searchFor, CancellationToken cancellationToken = default);

        // Item/Media methods
        Task<ArmoryItemMedia> GetItemMediaAsync(int itemId, string regionName = "us", CancellationToken cancellationToken = default);
        Task<ArmoryItemMedia> GetCreatureDisplayMediaAsync(long displayId, string regionName = "us", CancellationToken cancellationToken = default);

        // Realm methods
        Task<WowConnectedRealm> GetConnectedRealmInfoAsync(int realmId, string regionName = "us", CancellationToken cancellationToken = default);
        Task<WowConnectedRealm> GetConnectedRealmInfoAsync(string href, string regionName = "us", CancellationToken cancellationToken = default);
        Task<WowSingleRealmInfo> GetSingleRealmInfoAsync(string realmSlug, string regionName = "us", CancellationToken cancellationToken = default);
        Task<WowRealm> GetRealmStatusAsync(string locale, string region, CancellationToken cancellationToken = default);
        Task<WowRealmSearch.Root> GetRealmSearchAsync(string locale = "us", CancellationToken cancellationToken = default);
        Task<WowRealmSearch.Root> GetRealmSearchAsync(string locale, string regionName, CancellationToken cancellationToken = default);

        // Static data methods
        Task<Race> GetRacesAsync(CancellationToken cancellationToken = default);
        Task<WowClasses> GetWowClassesAsync(CancellationToken cancellationToken = default);
        Task<Achievements> GetWoWAchievementsAsync(CancellationToken cancellationToken = default);

        // Guild methods
        Task<GuildMembers> GetGuildMembersAsync(string realm, string guildName, string regionName = "us", CancellationToken cancellationToken = default);
        Task<GuildMembers> GetGuildMembersBySlugAsync(string slug, string guildName, string regionName = "us", CancellationToken cancellationToken = default);
        Task<GuildMembers> GetGuildMembersBySlugAsync(string slug, string guildName, string locale, string regionName = "us", CancellationToken cancellationToken = default);
        Task<GuildChar> GetCharFromGuildAsync(string findName, string realmName, string guildName, string regionName = "us", CancellationToken cancellationToken = default);

        // Journal methods
        Task<JournalEncounterResponse> GetJournalEncounterAsync(long encounterId, string region = "us", CancellationToken cancellationToken = default);
        Task<JournalInstanceResponse> GetJournalInstanceAsync(long instanceId, string region = "us", CancellationToken cancellationToken = default);
        Task<JournalEncounterIndexResponse> GetJournalEncounterIndexAsync(string region = "us", CancellationToken cancellationToken = default);
        Task<JournalInstanceIndexResponse> GetJournalInstanceIndexAsync(string region = "us", CancellationToken cancellationToken = default);
    }
}
