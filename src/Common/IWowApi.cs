using System.Threading;
using System.Threading.Tasks;

namespace NinjaBotCore.Common
{
    public interface IWowApi
    {
        void GetWowData();
        Task<string> GetAPIRequestAsync(string url, string locale, string region = "us", CancellationToken cancellationToken = default);
        Task<string> GetWowToken(string username, string password);
    }
}