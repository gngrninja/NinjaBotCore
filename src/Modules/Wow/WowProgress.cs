using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using System.IO;
using System.IO.Compression;
using System.Net;
using NinjaBotCore.Models.Wow;
using Newtonsoft.Json;
using System.Net.Http;
using System.Net.Http.Headers;

namespace NinjaBotCore.Modules.Wow
{
    class WowProgress
    {
        public WowProgress()
        {
            string baseURL = "http://www.wowprogress.com/export/ranks/";
            _links = this.GetLinks(baseURL);
        }

        public List<HtmlNode> _links;

        private static string FindRealmLink(List<HtmlNode> links, string realmName, string regionName = "us")
        {
            string baseURL = "http://www.wowprogress.com/export/ranks/";
            string url = string.Empty;
            string pattern = $"^{regionName}.+{realmName.ToLower()}.+\\.gz$";
            List<HtmlNode> possibleLinks = new List<HtmlNode>();
            foreach (HtmlNode link in links)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(link.InnerText, pattern))
                {
                    possibleLinks.Add(link);
                }
            }
            url = $"{baseURL}{possibleLinks.Select(l => l.InnerHtml).LastOrDefault()}";
            return url;
        }

        public byte[] DecompressFile(byte[] gzip)
        {
            // Create a GZIP stream with decompression mode.
            // ... Then create a buffer and write into while reading from the GZIP stream.
            using (GZipStream stream = new GZipStream(new MemoryStream(gzip),
                CompressionMode.Decompress))
            {
                const int size = 4096;
                byte[] buffer = new byte[size];
                using (MemoryStream memory = new MemoryStream())
                {
                    int count = 0;
                    do
                    {
                        count = stream.Read(buffer, 0, size);
                        if (count > 0)
                        {
                            memory.Write(buffer, 0, count);
                        }
                    }
                    while (count > 0);
                    return memory.ToArray();
                }
            }
        }

        // Note: This sync method is kept for constructor initialization.
        // For async contexts, use GetLinksAsync instead.
        public List<HtmlNode> GetLinks(string url)
        {
            string url_string = string.Empty;
            HtmlDocument doc = new HtmlDocument();
            using (var httpclient = new HttpClient())
            {
                url_string = httpclient.GetStringAsync(url).Result;
            }
            doc.LoadHtml(url_string);
            List<HtmlNode> links = new List<HtmlNode>();
            string sPattern = "^(us|eu).+\\.gz$";
            foreach (HtmlNode link in doc.DocumentNode.SelectNodes("//a[@href]"))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(link.InnerText, sPattern))
                {
                    links.Add(link);
                }
            }
            return links;
        }

        // ============================================================================
        // ASYNC METHODS - Use these instead of the sync versions above
        // ============================================================================

        public async Task<string> GetApiRequestAsync(string url, string regionName = "us", CancellationToken cancellationToken = default)
        {
            string fullUrl = $"http://www.wowprogress.com/guild/{regionName}/{url}/json_rank";
            Console.WriteLine(fullUrl);
            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return await httpClient.GetStringAsync(fullUrl, cancellationToken);
            }
        }

        public async Task<ProgressGuildRanks.GuildRank> GetGuildRankAsync(string guildName, string realmName, string regionName = "us", CancellationToken cancellationToken = default)
        {
            string url = $"{realmName.Replace("'", "-")}/{guildName.Replace(' ', '+')}";
            Console.WriteLine($"{realmName}/{guildName.Replace(' ', '+')}");
            var response = await GetApiRequestAsync(url, regionName, cancellationToken);
            return JsonConvert.DeserializeObject<ProgressGuildRanks.GuildRank>(response);
        }

        public async Task<byte[]> GetRankingsFileAsync(string url, CancellationToken cancellationToken = default)
        {
            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return await httpClient.GetByteArrayAsync(url, cancellationToken);
            }
        }

        public async Task<DateTime> GetLastModifyTimeAsync(string url, CancellationToken cancellationToken = default)
        {
            Console.WriteLine("I'm here in last modified async");
            using (HttpClient httpClient = new HttpClient())
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, new Uri(url));
                HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
                string lastModifyString = response.Content.Headers.LastModified.ToString();
                if (DateTime.TryParse(lastModifyString, out DateTime remoteTime))
                {
                    return remoteTime;
                }
                return DateTime.MinValue;
            }
        }

        public async Task<List<HtmlNode>> GetLinksAsync(string url, CancellationToken cancellationToken = default)
        {
            HtmlDocument doc = new HtmlDocument();
            using (var httpclient = new HttpClient())
            {
                var url_string = await httpclient.GetStringAsync(url, cancellationToken);
                doc.LoadHtml(url_string);
            }
            List<HtmlNode> links = new List<HtmlNode>();
            string sPattern = "^(us|eu).+\\.gz$";
            foreach (HtmlNode link in doc.DocumentNode.SelectNodes("//a[@href]"))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(link.InnerText, sPattern))
                {
                    links.Add(link);
                }
            }
            return links;
        }

        public async Task<List<ProgressGuildRanks.Ranking>> GetRealmObjectAsync(string realmName, List<HtmlNode> links, string regionName = "us", CancellationToken cancellationToken = default)
        {
            realmName = realmName.Replace("'", "-");
            string downloadURL = FindRealmLink(links, realmName, regionName);
            Console.WriteLine($"l{links} r{realmName}");
            DateTime remoteFileModified = await GetLastModifyTimeAsync(downloadURL, cancellationToken);
            DateTime localFileModified = new DateTime();

            if (File.Exists($"{realmName}-{regionName}.json"))
            {
                localFileModified = File.GetLastWriteTime($"{realmName}-{regionName}.json");
            }
            string bytesAsString = string.Empty;
            Console.WriteLine($"remote: {remoteFileModified} local: {localFileModified}");
            if (remoteFileModified > localFileModified)
            {
                byte[] fileDL = await GetRankingsFileAsync(downloadURL, cancellationToken);
                var decompressed = DecompressFile(fileDL);
                await File.WriteAllBytesAsync($"{realmName}-{regionName}.json", decompressed, cancellationToken);
                File.SetLastWriteTime($"{realmName}-{regionName}.json", await GetLastModifyTimeAsync(downloadURL, cancellationToken));
                bytesAsString = Encoding.ASCII.GetString(decompressed);
            }
            else
            {
                bytesAsString = Encoding.ASCII.GetString(await File.ReadAllBytesAsync($"{realmName}-{regionName}.json", cancellationToken));
            }
            return JsonConvert.DeserializeObject<List<ProgressGuildRanks.Ranking>>(bytesAsString);
        }
    }
}