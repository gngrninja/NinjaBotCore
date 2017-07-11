using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;
using NinjaBotCore.Models.Wow;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using NinjaBotCore.Database;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace NinjaBotCore.Modules.Wow
{
    public class WarcraftLogs
    {

        private static CancellationTokenSource _tokenSource;
        private static List<Zones> _zones;
        private static List<CharClasses> _charClasses;

        public WarcraftLogs()
        {
            Zones = this.GetZones();
            CharClasses = this.GetCharClasses();
        }

        public static List<Zones> Zones
        {
            get
            {
                return _zones;
            }
            private set
            {
                _zones = value;
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
        public static List<CharClasses> CharClasses
        {
            get
            {
                return _charClasses;
            }
            private set
            {
                _charClasses = value;
            }
        }

        public string logsApiRequest(string url)
        {
            string response = string.Empty;
            string wowLogsKey = $"api_key={Config.WarcraftLogsApi}";
            string baseURL = "https://www.warcraftlogs.com:443/v1";

            url = $"{baseURL}{url}{wowLogsKey}";
            Console.WriteLine($"Calling WarcraftLogs API with URL: {url}");

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

        public List<CharClasses> GetCharClasses()
        {
            List<CharClasses> charClasses;
            string url = "/classes?";

            charClasses = JsonConvert.DeserializeObject<List<CharClasses>>(logsApiRequest(url));

            return charClasses;
        }

        public List<Reports> GetReportsFromGuild(string guildName = "The benchwarmers", string realm = "Thunderlord", string region = "US")
        {
            string url = string.Empty;
            url = $"/reports/guild/{guildName.Replace(" ", "%20")}/{realm}/{region}?";
            List<Reports> logs;

            logs = JsonConvert.DeserializeObject<List<Reports>>(logsApiRequest(url));

            return logs;
        }

        public List<Reports> GetReportsFromUser(string userName)
        {
            string url = string.Empty;
            url = $"/reports/user/{userName.Replace(" ", "%20")}?";
            List<Reports> logs;

            logs = JsonConvert.DeserializeObject<List<Reports>>(logsApiRequest(url));

            return logs;
        }

        public List<CharParses> getParsesFromCharacterName(string charName, string realm, string region = "us")
        {
            List<CharParses> parse;
            string url = string.Empty;
            //https://www.warcraftlogs.com:443/v1/parses/character/oceanbreeze/thunderlord/US?zone=10&api_key=06cd4398efb2643988e1bb0e1387419a
            url = $"/parses/character/{charName}/{realm}/{region}?";

            parse = JsonConvert.DeserializeObject<List<CharParses>>(logsApiRequest(url));

            return parse;
        }

        public List<LogCharRankings> getRankingFromCharName(string charName, string realm, string region = "us")
        {
            List<LogCharRankings> charRankings;
            string url = string.Empty;

            url = $"/rankings/character/{charName}/{realm}/{region}?";

            charRankings = JsonConvert.DeserializeObject<List<LogCharRankings>>(logsApiRequest(url));

            return charRankings;
        }

        public List<LogCharRankings> GetRankingFromCharName(string charName, string realm, string zone, string region = "us")
        {
            List<LogCharRankings> charRankings;
            string url = string.Empty;
            int zoneID = 0;
            string findString = string.Empty;
            switch (zone)
            {
                case "en":
                    {
                        findString = "Emerald Nightmare";
                        break;
                    }
                case "tov":
                    {
                        findString = "Trial of Valor";
                        break;
                    }
                case "nh":
                    {
                        findString = "The Nighthold";
                        break;
                    }
                case "tos":
                    {
                        findString = "Tomb of Sargeras";
                        break;
                    }
            }
            zoneID = Zones.Where(z => z.name == findString).Select(z => z.id).FirstOrDefault();
            url = $"/rankings/character/{charName}/{realm}/{region}?zone={zoneID}&";
            charRankings = JsonConvert.DeserializeObject<List<LogCharRankings>>(logsApiRequest(url));
            return charRankings;
        }

        public List<Zones> GetZones()
        {
            string url = string.Empty;
            url = "/zones?";
            List<Zones> zones;

            zones = JsonConvert.DeserializeObject<List<Zones>>(logsApiRequest(url));

            return zones;
        }

        public Fights GetFights(string code)
        {
            string url = string.Empty;
            Fights fights;
            url = $"/report/fights/{code}?";

            fights = JsonConvert.DeserializeObject<Fights>(logsApiRequest(url));

            return fights;
        }

        public ReportTable GetReportResults(string fightID, string view = "damage-done")
        {
            List<Reports> reports = GetReportsFromGuild();
            Fights fights = GetFights(reports[0].id);
            List<Fight> bossFights = new List<Fight>();

            foreach (Fight fight in fights.fights)
            {
                if (fight.boss != 0)
                {
                    bossFights.Add(fight);
                }
            }

            string url = $"/report/tables/{view}/{reports[0].id}?start={bossFights[0].start_time}&end={bossFights[0].end_time}&";

            ReportTable table = JsonConvert.DeserializeObject<ReportTable>(logsApiRequest(url));

            return table;
        }

        public ReportTable GetReportResults(bool getLastFight, string view = "damage-done")
        {
            List<Reports> reports = GetReportsFromGuild();
            Fights fights = GetFights(reports[0].id);
            List<Fight> bossFights = new List<Fight>();

            foreach (Fight fight in fights.fights)
            {
                if (fight.boss != 0)
                {
                    bossFights.Add(fight);
                }
            }

            string url = $"/report/tables/{view}/{reports[0].id}?start={bossFights[0].start_time}&end={bossFights[0].end_time}&";

            ReportTable table = JsonConvert.DeserializeObject<ReportTable>(logsApiRequest(url));

            return table;
        }

        public WarcraftlogRankings.RankingObject GetRankingsByEncounter(int encounterID, string realmName, string metric = "dps", int difficulty = 4, string regionName = "us")
        {
            WarcraftlogRankings.RankingObject l = new WarcraftlogRankings.RankingObject();
            string url = $"/rankings/encounter/{encounterID}?metric={metric}&server={realmName}&region={regionName}&difficulty={difficulty}&limit=1000&";
            l = JsonConvert.DeserializeObject<WarcraftlogRankings.RankingObject>(logsApiRequest(url));
            return l;
        }

        public WarcraftlogRankings.RankingObject GetRankingsByEncounterGuild(int encounterID, string realmName, string guildName, string metric = "dps", int difficulty = 4, string regionName = "us")
        {
            WarcraftlogRankings.RankingObject l = new WarcraftlogRankings.RankingObject();
            guildName = guildName.Replace(" ", "%20");
            string url = $"/rankings/encounter/{encounterID}?guild={guildName}&server={realmName}&region={regionName}&metric={metric}&difficulty={difficulty}&limit=1000&";
            l = JsonConvert.DeserializeObject<WarcraftlogRankings.RankingObject>(logsApiRequest(url));
            return l;
        }

        public DateTime UnixTimeStampToDateTime(long unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddMilliseconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }

        public async Task WarcraftLogsTimer(Action action, TimeSpan interval, CancellationToken token)
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
            var timerAction = new Action(CheckForNewLogs);
            await WarcraftLogsTimer(timerAction, TimeSpan.FromSeconds(15), TokenSource.Token);
        }

        public async Task StopTimer()
        {
            TokenSource.Cancel();
        }

        async void CheckForNewLogs()
        {
            List<WowGuildAssociations> guildList = null;
            List<LogMonitoring> logWatchList = null;
            using (var db = new NinjaBotEntities())
            {
                guildList = db.WowGuildAssociations.ToList();
                logWatchList = db.LogMonitoring.ToList();
                
            }
            if (guildList != null)
            {
                foreach (var guild in guildList)
                {
                    var watchGuild = logWatchList.Where(w => w.ServerId == guild.ServerId).FirstOrDefault();
                    if (watchGuild.MonitorLogs)
                    {
                        System.Console.WriteLine($"YES! Watch logs on {guild.ServerName}!");
                        var logs = GetReportsFromGuild(guild.WowGuild, guild.WowRealm, guild.WowRegion);
                        if (logs != null)
                        {
                            var latestLog = logs[logs.Count - 1];
                            DateTime startTime = UnixTimeStampToDateTime(latestLog.start);
                            if (startTime > watchGuild.LatestLog)
                            {
                                using (var db = new NinjaBotEntities())
                                {
                                    var latestForGuild = db.LogMonitoring.Where(l => l.ServerId == guild.ServerId).FirstOrDefault();
                                    latestForGuild.LatestLog = startTime;
                                    await db.SaveChangesAsync();
                                }
                                System.Console.WriteLine($"New log found for [{guild.WowGuild}] on [{guild.WowRegion}] for Discord server [{guild.ServerName}] -> log start time [{startTime}]");
                                DiscordSocketClient client = NinjaBot.Client;
                                ISocketMessageChannel channel = client.GetChannel((ulong)watchGuild.ChannelId) as ISocketMessageChannel;
                                var embed = new EmbedBuilder();
                                embed.Title = $"New log found for [{guild.WowGuild}]!";
                                StringBuilder sb = new StringBuilder();
                                sb.AppendLine($"__**{latestLog.title}** **/** **{latestLog.zoneName}**__");
                                sb.AppendLine($"\t:timer: Start time: **{UnixTimeStampToDateTime(latestLog.start)}**");
                                if (!string.IsNullOrEmpty(latestLog.end.ToString()))
                                {
                                    sb.AppendLine($"\t:stopwatch: End time: **{UnixTimeStampToDateTime(latestLog.end)}**");
                                }                                
                                sb.AppendLine($"\tLink: **{latestLog.reportURL}**");
                                sb.AppendLine($"\t:white_check_mark: My WoW: **http://www.checkmywow.com/reports/{latestLog.id}**");
                                sb.AppendLine();
                                embed.Description = sb.ToString();
                                embed.WithColor(new Color(0, 0, 255));
                                await channel.SendMessageAsync("", false, embed);
                            }
                        }
                    }
                }
            }
        }
    }
}