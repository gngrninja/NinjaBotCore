using System.Threading.Tasks;
namespace NinjaBotCore.Common
{
    public interface IWowApi
    {
        void GetWowData();
        string GetAPIRequest(string url, string locale);
        string GetAPIRequest(string url, string locale, string region = "us");
        public Task<string> GetWowToken(string username, string password);
    }
}