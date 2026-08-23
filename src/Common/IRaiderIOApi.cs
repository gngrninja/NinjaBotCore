using System.Threading;
using System.Threading.Tasks;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Common
{
    public interface IRaiderIOApi
    {
        Task<RaiderIOModels.Affix> GetCurrentAffixAsync(string region = "us", string locale = "en", CancellationToken cancellationToken = default);
        Task<RaiderIOModels.RioGuildInfo> GetRioGuildInfoAsync(string guildName, string realmName, string region, CancellationToken cancellationToken = default);
        Task<RaiderIOModels.RioMythicPlusChar> GetCharMythicPlusInfoAsync(string charName, string realmName, string region = "us", CancellationToken cancellationToken = default);
        Task<RaiderIOModels.RioMythicPlusChar> GetCharInsightsInfoAsync(string charName, string realmName, string region = "us", CancellationToken cancellationToken = default);
        Task<RaiderIOModels.MythicPlusStaticData> GetMythicPlusStaticDataAsync(int expansionId, CancellationToken cancellationToken = default);
        Task<RaiderIOModels.CharacterRivalsResponse> GetCharacterRivalsAsync(string charName, string realmName, string region, string scope = "region", long? specId = null, CancellationToken cancellationToken = default);
        Task<RaiderIOModels.RunReviewResponse> GetRunReviewAsync(string charName, string realmName, string region, RaiderIOModels.MythicPlusRun run, string scope = "region", CancellationToken cancellationToken = default);
        Task<RaiderIOModels.SeasonCutoffsResponse> GetSeasonCutoffsAsync(string region, string season, CancellationToken cancellationToken = default);
        Task<RaiderIOModels.LeaderboardCapacityResponse> GetLeaderboardCapacityAsync(string region, string realm = null, string scope = "current", CancellationToken cancellationToken = default);
        Task<RaiderIOModels.GuildLiveRaidResponse> GetGuildLiveRaidProgressAsync(string guildName, string realmName, string region, string raid = "latest", string difficulty = "latest", CancellationToken cancellationToken = default);
    }
}
