using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        public string GetApiRequest(string url, string regionName = "us")
        {
            string fullUrl = $"http://www.wowprogress.com/guild/{regionName}/{url}/json_rank";
            Console.WriteLine(fullUrl);
            string response = string.Empty;
            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));
                response = httpClient.GetStringAsync(fullUrl).Result;
            }
            return response;
        }

        public ProgressGuildRanks.GuildRank GetGuildRank(string guildName, string realmName, string regionName = "us")
        {
            string url = $"{realmName.Replace("'", "-")}/{guildName.Replace(' ', '+')}";
            Console.WriteLine($"{realmName}/{guildName.Replace(' ', '+')}");
            ProgressGuildRanks.GuildRank rank = new ProgressGuildRanks.GuildRank();
            rank = JsonConvert.DeserializeObject<ProgressGuildRanks.GuildRank>(GetApiRequest(url, regionName));
            return rank;
        }

        public List<ProgressGuildRanks.Ranking> GetRealmObject(string realmName, List<HtmlNode> links, string regionName = "us")
        {
            realmName = realmName.Replace("'", "-");
            string downloadURL = FindRealmLink(links, realmName, regionName);
            Console.WriteLine($"l{links} r{realmName}");
            DateTime remoteFileModified = GetLastModifyTime(downloadURL);
            DateTime localFileModified = new DateTime();

            if (File.Exists($"{realmName}-{regionName}.json"))
            {
                localFileModified = File.GetLastWriteTime($"{realmName}-{regionName}.json");
            }
            string bytesAsString = string.Empty;
            Console.WriteLine($"remote: {remoteFileModified} local: {localFileModified}");
            if (remoteFileModified > localFileModified)
            {

                byte[] fileDL = GetRankingsFile(downloadURL);
                var decompressed = DecompressFile(fileDL);
                File.WriteAllBytes($"{realmName}-{regionName}.json", decompressed);
                File.SetLastWriteTime($"{realmName}-{regionName}.json", GetLastModifyTime(downloadURL));
                bytesAsString = Encoding.ASCII.GetString(decompressed);
            }
            else
            {
                bytesAsString = Encoding.ASCII.GetString(File.ReadAllBytes($"{realmName}-{regionName}.json"));
            }
            var realmObject = JsonConvert.DeserializeObject<List<ProgressGuildRanks.Ranking>>(bytesAsString);
            return realmObject;
        }

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

        public byte[] GetRankingsFile(string url)
        {
            byte[] fileDL;
            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));

                fileDL = httpClient.GetByteArrayAsync(url).Result;
            }
            return fileDL;
        }

        public DateTime GetLastModifyTime(string url)
        {
            System.Console.WriteLine("I'm here in last modified");
            using (HttpClient httpClient = new HttpClient())
            {
                HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, new Uri(url));
                HttpResponseMessage response = httpClient.SendAsync(request).Result;                
                string lastModifyString = response.Content.Headers.LastModified.ToString();
                DateTime remoteTime;
                if (DateTime.TryParse(lastModifyString, out remoteTime))
                {
                   return remoteTime;
                }
                return DateTime.MinValue;
            }
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
    }
}