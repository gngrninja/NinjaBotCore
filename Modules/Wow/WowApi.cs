using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using NinjaBotCore.Models.Wow;
using System.IO;
using NinjaBotCore.Database;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace NinjaBotCore.Modules.Wow
{
    public class WowApi
    {
        private static string _token;
        private static WowClasses _classes;
        private static Race _race;
        private static List<Achievement2> _achievements;
        private static WowRealm _realmInfo;
        private static WowRealm _realmInfoEu;
        private static WowRealm _realmInfoRu;
        private readonly IConfigurationRoot _config;
        private static CancellationTokenSource _tokenSource;
        private readonly ILogger _logger;

        public WowApi(IConfigurationRoot config, ILogger<WowApi> logger)
        {
            try
            {
                _config = config;
                _logger = logger;
                this.StartTimer();
                //_token = GetWoWToken(username: _config["WoWClient"], password: _config["WoWSecret"]);
                Races = this.GetRaces();
                Classes = this.GetWowClasses();
                Achievements cheeves = this.GetWoWAchievements();
                Achievements = cheeves.achievements.Select(m => m.categories).Skip(1).SelectMany(i => i).Select(a => a.achievements).SelectMany(d => d).ToList();
                RealmInfo = this.GetRealmStatus("us");
                RealmInfoEu = this.GetRealmStatus("eu");  
                RealmInfoRu = this.GetRealmStatus("ru_RU","eu");
                
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error creating WowApi class -> [{ex.Message}]");
            }
        }

        public static WowRealm RealmInfo
        {
            get
            {
                return _realmInfo;
            }
            private set
            {
                _realmInfo = value;
            }
        }
        public static WowRealm RealmInfoEu
        {
            get
            {
                return _realmInfoEu;
            }
            private set
            {
                _realmInfoEu = value;
            }
        }
        public static WowRealm RealmInfoRu
        {
            get
            {
                return _realmInfoRu;
            }
            private set
            {
                _realmInfoRu = value;
            }
        }
        public static List<Achievement2> Achievements
        {
            get
            {
                return _achievements;
            }
            private set
            {
                _achievements = value;
            }
        }

        public static Race Races
        {
            get
            {
                return _race;
            }
            private set
            {
                _race = value;
            }
        }

        public static WowClasses Classes
        {
            get
            {
                return _classes;
            }
            private set
            {
                _classes = value;
            }
        }

        public static CancellationTokenSource TokenSource
        {
            get
            {
                return _tokenSource;
            }
            set
            {
                _tokenSource = value;
            }
        }

        public WowClasses wowclasses;

        //public TalentList wowtalents;

        public string GetAPIRequest(string url, string region = "us")
        {
            string response;
            string key;
            string prefix;

            region = region.ToLower();

            prefix = $"https://{region}.api.blizzard.com/wow";
            key = $"&access_token={_token}";
            url = $"{prefix}{url}{key}";

            _logger.LogInformation($"Wow API request to {url}");

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));
                //test = httpClient.PostAsJsonAsync<FaceRequest>(fullUrl, request).Result;                             
                response = httpClient.GetStringAsync(url).Result;
            }

            return response;
        }

        public string GetAPIRequest(string url, string locale, string region = "us")
        {
            string response;
            string key;
            string prefix;
            //httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "Your Oauth token")
            region = region.ToLower();
            prefix = $"https://{region}.api.blizzard.com/wow";
            key = $"&access_token={_token}"; 

            if (!url.Contains('='))
            {
                locale = $"locale={locale}";
            }      
            else 
            {
                locale = $"&locale={locale}";
            }         

            url = $"{prefix}{url}{locale}{key}";

            _logger.LogInformation($"Wow API request to {url}");

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));
                //test = httpClient.PostAsJsonAsync<FaceRequest>(fullUrl, request).Result;                             
                response = httpClient.GetStringAsync(url).Result;
            }
        
            return response;
        }

        public string GetAPIRequest(string url, bool fileDownload)
        {
            string response;
            url = $"{url}";

            _logger.LogInformation($"Wow API request to {url}");

            using (HttpClient httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders
                    .Accept
                    .Add(new MediaTypeWithQualityHeaderValue("application/json"));
                response = httpClient.GetStringAsync(url).Result;
            }
            
            return response;
        }

        public static string GetWoWToken(string username, string password) {
            string token = string.Empty;
            HttpClient client = new HttpClient();
            //httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "Your Oauth token");            
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", username),
                new KeyValuePair<string, string>("client_secret", password)
            });
            var result =  client.PostAsync("https://us.battle.net/oauth/token", content);
            var contentString =  result.Result.Content.ReadAsStringAsync();
            ApiResponse response = JsonConvert.DeserializeObject<ApiResponse>(contentString.Result);
            token = response.AccessToken;
            return token;
        }

        public WowRealm GetRealmStatus(string locale = "us")
        {
            string localeName = GetRegionFromString(locale);
            WowRealm w = new WowRealm();
            string url = $"/realm/status?";
            w = JsonConvert.DeserializeObject<WowRealm>(GetAPIRequest(url, localeName, locale));
            return w;
        }

        public WowRealm GetRealmStatus(string locale, string region)
        {
            string localeName = string.Empty;
            
            if (locale.Length == 5)
            {
                localeName = GetRegionFromString(locale.Substring(3).ToLower());
            }
            else if (locale.Length == 2)
            {
                localeName = GetRegionFromString(locale);
            }
            
            WowRealm w = new WowRealm();
            string url = $"/realm/status?";
            w = JsonConvert.DeserializeObject<WowRealm>(GetAPIRequest(url, locale: localeName, region: region));
            return w;
        }
        public async Task<List<WowAuctions>> GetAuctionsByRealm(string realmName, string regionName = "us")
        {
            AuctionsModel.AuctionFile file;
            AuctionsModel.Auctions a = new AuctionsModel.Auctions();
            AuctionsModel.Auction[] auctions;
            string url = string.Empty;
            string fileContent = string.Empty;
            DateTime? latestTimeStampFromDb;
            List<WowAuctions> dbAuctions = new List<WowAuctions>();
            List<WowAuctions> returnAuction = new List<WowAuctions>();
            
            url = $"/auction/data/{realmName}?locale={regionName}";

            file = JsonConvert.DeserializeObject<AuctionsModel.AuctionFile>(GetAPIRequest(url, regionName));
            string fileURL = file.files[0].url;
            DateTime lastModified = UnixTimeStampToDateTime(file.files[0].lastModified);

            using (var db = new NinjaBotEntities())
            {
                dbAuctions = db.WowAuctions.Where(r => r.RealmName.ToLower() == realmName.ToLower()).ToList();
                latestTimeStampFromDb = dbAuctions.OrderBy(t => t.DateModified).Take(1).Select(l => l.DateModified).FirstOrDefault();
                //db.Database.CurrentTransaction.Dispose();
                //db.Database.Connection.Close();
            }

            if (dbAuctions.Count > 0)
            {
                if (lastModified > latestTimeStampFromDb)
                {
                    fileContent = GetAPIRequest(fileURL, true);
                }
                else
                {
                    return dbAuctions;
                }
            }
            else
            {
                fileContent = GetAPIRequest(fileURL, true);
            }
            a = JsonConvert.DeserializeObject<AuctionsModel.Auctions>(fileContent);
            auctions = a.auctions;
            auctions[0].fileDate = lastModified;
            await AddAuctionsToDb(realmName, a, auctions, lastModified);
            using (var db = new NinjaBotEntities())
            {
                string slugName = a.realms[0].slug;
                returnAuction = db.WowAuctions.Where(w => w.RealmSlug == slugName).ToList();
                //db.Database.Connection.Close();
            }
            return returnAuction;
        }

        private async Task AddAuctionsToDb(string realmName, AuctionsModel.Auctions a, AuctionsModel.Auction[] auctions, DateTime lastModified)
        {
            using (var db = new NinjaBotEntities())
            {
                List<WowAuctions> dbAuctions = new List<WowAuctions>();
                //db.Configuration.AutoDetectChangesEnabled = false;
                //db.Configuration.ValidateOnSaveEnabled = false;
                try
                {
                    string slugName = a.realms[0].slug;
                    dbAuctions = db.WowAuctions.Where(w => w.RealmSlug == slugName).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message);
                }
                if (dbAuctions.Count > 0)
                {
                    DateTime? latestTimeStamp = dbAuctions.OrderBy(t => t.DateModified).Take(1).Select(l => l.DateModified).FirstOrDefault();
                    if (!string.IsNullOrEmpty(latestTimeStamp.Value.ToString("d")))
                    {
                        if (lastModified > latestTimeStamp)
                        {
                            var delThese = db.WowAuctions.Where(d => d.DateModified != lastModified);
                            db.WowAuctions.RemoveRange(delThese);
                            await db.SaveChangesAsync();
                            foreach (var auction in auctions)
                            {
                                db.WowAuctions.Add(new WowAuctions
                                {
                                    RealmName = realmName,
                                    AuctionBid = auction.bid,
                                    AuctionBuyout = auction.buyout,
                                    AuctionContext = auction.context,
                                    WowAuctionId = auction.auc,
                                    RealmSlug = a.realms[0].slug,
                                    AuctionItemId = auction.item,
                                    AuctionOwner = auction.owner,
                                    AuctionOwnerRealm = auction.ownerRealm,
                                    AuctionRand = auction.rand,
                                    AuctionQuantity = auction.quantity,
                                    AuctionTimeLeft = auction.timeLeft,
                                    AuctionSeed = auction.seed,
                                    DateModified = lastModified
                                });
                            }
                            _logger.LogInformation("New Auctions... Saving changes to DB");
                        }
                    }
                }
                else
                {
                    foreach (var auction in auctions)
                    {
                        db.WowAuctions.Add(new WowAuctions
                        {
                            RealmName = realmName,
                            AuctionBid = auction.bid,
                            AuctionBuyout = auction.buyout,
                            AuctionContext = auction.context,
                            WowAuctionId = auction.auc,
                            RealmSlug = a.realms[0].slug,
                            AuctionItemId = auction.item,
                            AuctionOwner = auction.owner,
                            AuctionOwnerRealm = auction.ownerRealm,
                            AuctionRand = auction.rand,
                            AuctionQuantity = auction.quantity,
                            AuctionTimeLeft = auction.timeLeft,
                            AuctionSeed = auction.seed,
                            DateModified = lastModified
                        });
                    }
                    _logger.LogInformation("Saving changes to DB");
                }
                await db.SaveChangesAsync();
                //db.Database.Connection.Close();
            }
        }

        public Character GetCharInfo(string name, string realm, string regionName = "us")
        {
            string url;
            string region = string.Empty;
            region = GetRegionFromString(regionName);
            Character c = new Character();
            realm = realm.Replace("'", string.Empty).Replace(" ", "-");
            url = $"/character/{realm}/{name}?fields=achievements,talents,items,stats&locale={region}";
            if (region != "en_US")
            {
                c = JsonConvert.DeserializeObject<Character>(GetAPIRequest(url, "eu"));
            }
            else
            {
                c = JsonConvert.DeserializeObject<Character>(GetAPIRequest(url));
            }
            string thumbUrl = $"http://render-{regionName}.worldofwarcraft.com/character/{c.thumbnail}";
            c.thumbnailURL = thumbUrl;
            string insetUrl = $"http://render-{regionName}.worldofwarcraft.com/character/{c.thumbnail.Replace("-avatar", "-inset")}";
            c.insetURL = insetUrl;
            string profilePicUrl = $"http://render-{regionName}.worldofwarcraft.com/character/{c.thumbnail.Replace("-avatar", "-profilemain")}";
            c.profilePicURL = profilePicUrl;
            string armoryUrl = $"http://{regionName}.battle.net/wow/en/character/{c.realm}/{c.name}/advanced";
            c.armoryURL = armoryUrl;
            return c;
        }

        private static string GetRegionFromString(string regionName)
        {
            string region;
            switch (regionName.ToLower())
            {
                case "us":
                    {
                        region = "en_US";
                        break;
                    }
                case "uk":
                    {
                        region = "en_GB";
                        break;
                    }
                case "gb":
                    {
                        region = "en_GB";
                        break;
                    }
                case "eu":
                    {
                        region = "en_GB";
                        break;
                    }
                case "ru":
                    {
                        region = "ru_RU";
                        break;
                    }
                default:
                    {
                        region = "en_US";
                        break;
                    }
            }
            return region;
        }

        public ItemInfo GetItemInfo(int itemID)
        {
            string url;
            ItemInfo i;

            url = $"/item/{itemID}?locale=en";

            i = JsonConvert.DeserializeObject<ItemInfo>(GetAPIRequest(url));

            return i;
        }

        public Race GetRaces()
        {
            string url;
            Race r;

            url = "/data/character/races?locale=en_US";
            r = JsonConvert.DeserializeObject<Race>(GetAPIRequest(url));

            return r;
        }

        public WowClasses GetWowClasses()
        {
            WowClasses c;
            string url;

            url = "/data/character/classes?locale=en_US";
            c = JsonConvert.DeserializeObject<WowClasses>(GetAPIRequest(url));

            return c;
        }

        public TalentList GetWowTalents()
        {
            TalentList talents;
            string url;

            url = "/data/talents?locale=en_US";

            talents = JsonConvert.DeserializeObject<TalentList>(GetAPIRequest(url));

            return talents;
        }

        public Achievements GetWoWAchievements()
        {
            Achievements a;
            string url;

            url = "/data/character/achievements?locale=en_US";
            a = JsonConvert.DeserializeObject<Achievements>(GetAPIRequest(url));

            return a;
        }

        public CharAchievements GetCharAchievements(string charName, string realmName)
        {
            AchievementChar charAchievements;

            string url = $"/character/{realmName}/{charName}?fields=achievements&locale=en_US";

            charAchievements = JsonConvert.DeserializeObject<AchievementChar>(GetAPIRequest(url));

            CharAchievements c = charAchievements.achievements;

            return c;
        }

        public GuildMembers GetGuildMembers(string realm, string guildName, string regionName = "us")
        {
            string url;
            GuildMembers g;
            string locale = GetRegionFromString(regionName);
            url = $"/guild/{realm}/{guildName}?fields=members";
            if (locale != "en_US")
            {
                g = JsonConvert.DeserializeObject<GuildMembers>(GetAPIRequest(url, region: "eu", locale: locale ));
            }
            else
            {
                g = JsonConvert.DeserializeObject<GuildMembers>(GetAPIRequest(url));
            }
            return g;
        }

        public GuildMembers GetGuildMembers(string realm, string guildName, string locale, string regionName = "us")
        {
            string url;
            GuildMembers g;
            url = $"/guild/{realm}/{guildName}?fields=members";
            if (locale != "en_US")
            {
                g = JsonConvert.DeserializeObject<GuildMembers>(GetAPIRequest(url, region: "eu", locale: locale));
            }
            else
            {
                g = JsonConvert.DeserializeObject<GuildMembers>(GetAPIRequest(url));
            }
            return g;
        }

        public WoWTalentMain GetCharTalents(string charName, string realmName)
        {
            WoWTalentMain charTalents;

            string url;

            url = $"/character/{realmName}/{charName}?fields=talents&locale=en_US";

            charTalents = JsonConvert.DeserializeObject<WoWTalentMain>(GetAPIRequest(url));

            return charTalents;
        }

        public WowStats GetCharStats(string charName, string realmName, string locale)
        {
            string region = string.Empty;
            WowStats g;
            string url;

            if (locale != "en_US")
            {
                region = "eu";
            }
            else
            {
                region = "us";
            }

            url = $"/character/{realmName}/{charName}?fields=statistics&locale={locale}";
            g = JsonConvert.DeserializeObject<WowStats>(GetAPIRequest(url,region: region));

            return g;
        }

        public DateTime UnixTimeStampToDateTime(long unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddMilliseconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        public DateTime UnixTimeStampToDateTimeSeconds(long unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        public GuildChar GetCharFromGuild(string findName, string realmName, string guildName, string regionName = "us")
        {
            GuildMembers members = new GuildMembers();
            string matchedName = string.Empty;
            GuildChar guildInfo = new GuildChar();
            Regex myRegex = new Regex($@"{findName.ToLower()}");
            guildName = guildName.Replace(" ", "%20");
            try
            {
                members = GetGuildMembers(realmName, guildName, regionName);
            }
            catch (Exception ex)
            {
                _logger.LogError($"{ex.Message}");
            }
            foreach (Member member in members.members)
            {
                string curMember = string.Empty;

                curMember = member.character.name;

                MatchCollection m = myRegex.Matches(curMember.ToLower());

                switch (m.Count)
                {
                    case 1:
                        {
                            matchedName = curMember;
                            realmName = member.character.realm;

                            guildInfo.charName = curMember;
                            guildInfo.realmName = realmName;
                            guildInfo.regionName = regionName;

                            break;
                        }
                    default:
                        {
                            break;
                        }
                }
            }
            return guildInfo;
        }

        public List<FoundChar> SearchArmory(string searchFor)
        {
            string url = $"https://worldofwarcraft.com/en-us/search?q={searchFor}";
            string url_string = string.Empty;
            HtmlDocument document = new HtmlDocument();
            using (var httpclient = new HttpClient())
            {
                url_string = httpclient.GetStringAsync(url).Result;
            }
            document.LoadHtml(url_string);
            List<FoundChar> chars = new List<FoundChar>();
            FoundChar found = new FoundChar();
            try
            {
                foreach (HtmlNode div in document.DocumentNode.SelectNodes("//div[contains(@class,'Character-')]"))
                {

                    if ((div.Attributes[0].Value) == "Character-name")
                    {
                        found.charName = div.InnerText;
                    }

                    if ((div.Attributes[0].Value) == "Character-level")
                    {
                        found.level = div.InnerText;
                    }

                    if ((div.Attributes[0].Value) == "Character-realm")
                    {
                        found.realmName = div.InnerText;
                        chars.Add(found);
                        found = new FoundChar();
                    }

                }

            }
            catch (Exception ex)
            {
                _logger.LogError($"WoW Char Search Error: {ex.Message}");
                chars = null;
            }
            return chars;
        }
        public async Task WoWTokenTimer(Action action, TimeSpan interval, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    action();
                    await Task.Delay(interval, token);
                }
            }
            catch (TaskCanceledException ex)
            {

            }
        }

        public async Task StartTimer()
        {
            TokenSource = new CancellationTokenSource();
            var timerAction = new Action(RenewTokenLocal);
            await WoWTokenTimer(timerAction, TimeSpan.FromSeconds(43200), TokenSource.Token);
        }

        public async Task StopTimer()
        {
            TokenSource.Cancel();
        }

        private void RenewTokenLocal()
        {
            _logger.LogInformation("Renewing token!");
            _token = GetWoWToken(username: _config["WoWClient"], password: _config["WoWSecret"]);
        }
    }
}