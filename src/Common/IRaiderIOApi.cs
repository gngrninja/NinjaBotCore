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
        Task<RaiderIOModels.MythicPlusStaticData> GetMythicPlusStaticDataAsync(int expansionId, CancellationToken cancellationToken = default);
    }
}
