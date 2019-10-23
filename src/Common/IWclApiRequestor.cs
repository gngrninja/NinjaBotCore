using System.Net.Http;
using System.Threading.Tasks;

namespace NinjaBotCore.Common
{
    public interface IWclApiRequestor
    {        
        Task<T> Get<T>(string relativeUrl);
    }
}
