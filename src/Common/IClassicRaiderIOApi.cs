using System.Threading;
using System.Threading.Tasks;
using NinjaBotCore.Models.Wow;

namespace NinjaBotCore.Common
{
    public interface IClassicRaiderIOApi
    {
        Task<ClassicRaiderIOModels.ClassicCharProfile> GetCharacterProfileAsync(
            string charName, string realmName, string region = "us", CancellationToken cancellationToken = default);

        Task<ClassicRaiderIOModels.ClassicGuildProfile> GetGuildProfileAsync(
            string guildName, string realmName, string region = "us", CancellationToken cancellationToken = default);
    }
}
