using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;
using System.Text.RegularExpressions;
using NinjaBotCore.Models.RocketLeague;
using System.Net.Http;

namespace NinjaBotCore.Modules.RocketLeague
{
    public class RocketLeague
    {               
        public async Task<List<RocketLeagueStats>> getRLStats(string rlUserName)
        {
            string statsPage = "https://rocketleague.tracker.network/profile/steam/";
            string fullURL = statsPage + rlUserName;
            string foundText = string.Empty;
            string pattern = "\\s+";
            string replacement = " ";
            StringBuilder sb = new StringBuilder();
            Regex rgx = new Regex(pattern);
            //var web = new HtmlWeb();
            var document = new HtmlDocument();
            string url_string = string.Empty;
            var stats = new List<RocketLeagueStats>();
            bool seasonEnderFound = false;            

            using (var httpclient = new HttpClient())
            {
                url_string = await httpclient.GetStringAsync(fullURL);
            }
            //var webGet = new HtmlAgilityPack.HtmlWeb();

            document.LoadHtml(url_string);

            var nodes = document.DocumentNode.Descendants("tr");

            sb.AppendLine();
            int oi = 0;
            int nodeCount = 0;
            foreach (var node in nodes.Skip(1))
            {
                nodeCount++;
                Console.WriteLine($"Node count: [{nodeCount}]");
                if (!seasonEnderFound)
                {                
                    if (oi == 4)
                    {
                        seasonEnderFound = true;
                    }                    
                    if (!String.IsNullOrWhiteSpace(node.InnerText))
                    {                        
                        string nodeText = node.InnerText;
                        nodeText = rgx.Replace(nodeText, replacement);
                        if (nodeText.ToLower().Contains("standard") & nodeText.ToLower().Contains("top") && !(nodeText.ToLower().Contains("solo")))
                        {                            
                            oi++;
                            bool placementMatches = false;
                            var rankText = nodeText.Trim().Split(' ').ToList();
                            if (rankText.Count() > 4)
                            {
                                rankText.ForEach(r =>
                                {
                                    if (r.Trim() == "100%)")
                                    {
                                        stats.Add(new RocketLeagueStats
                                        {
                                            GamesPlayed = "N/A",
                                            Title = "N/A",
                                            Rank = "N/A",
                                            Division = "N/A",
                                            MMR = 0,
                                            Percentage = "N/A"
                                        });

                                        placementMatches = true;
                                    }
                                });
                                if (!placementMatches)
                                {
                                    int textCount = rankText.Count() - 6;
                                    string Title = $"{rankText[1]} {rankText[2]}";
                                    string Rank = string.Empty;
                                    string Division = string.Empty;
                                    string Percentage = string.Empty;
                                    string MMR = string.Empty;
                                    string GamesPlayed = string.Empty;
                                    int i = 3;
                                    bool divisionFound = false;
                                    do
                                    {
                                        if (rankText[i].ToLower() != "division")
                                        {
                                            Rank += $"{rankText[i]} ";
                                            i++;
                                        }
                                        else
                                        {
                                            Division = rankText[i + 1];
                                            if (rankText[i + 2].Contains("~"))
                                            {
                                                MMR = rankText[i + 3];
                                            }
                                            else
                                            {
                                                MMR = rankText[i + 2];
                                            }
                                            //GamesPlayed = rankText[i + 5];
                                            GamesPlayed = "N/A";
                                            //Percentage = $"Top {rankText[i + 5].Replace('%', ' ').Replace(')', ' ').Trim()}%";
                                            Percentage = "N/A";
                                            divisionFound = true;
                                        }
                                    }
                                    while (divisionFound == false);
                                    stats = await AddStats(stats, Title, Rank, Division, MMR, fullURL);
                                }
                            }
                        }
                            
                        if (nodeText.ToLower().Contains("duel") && nodeText.ToLower().Contains("top"))
                        {
                            oi++;
                            bool placementMatches = false;
                            var rankText = nodeText.Trim().Split(' ').ToList();
                            if (rankText.Count() > 4)
                            {
                                rankText.ForEach(r =>
                                {
                                    if (r.Trim() == "100%)")
                                    {
                                        stats.Add(new RocketLeagueStats
                                        {
                                            GamesPlayed = "N/A",
                                            Title = "N/A",
                                            Rank = "N/A",
                                            Division = "N/A",
                                            MMR = 0,
                                            Percentage = "N/A"
                                        });

                                        placementMatches = true;
                                    }
                                    else
                                    {
                                        //Console.WriteLine(r);
                                    }
                                });

                                if (!placementMatches)
                                {
                                    int textCount = rankText.Count() - 6;
                                    string Title = $"{rankText[1]} {rankText[2]}";
                                    string Rank = string.Empty;
                                    string Division = string.Empty;
                                    string Percentage = string.Empty;
                                    string MMR = string.Empty;
                                    string GamesPlayed = string.Empty;
                                    int i = 3;
                                    bool divisionFound = false;
                                    do
                                    {
                                        if (rankText[i].ToLower() != "division")
                                        {
                                            Rank += $"{rankText[i]} ";
                                            i++;
                                        }
                                        else
                                        {
                                            Division = rankText[i + 1];
                                            if (rankText[i + 2].Contains("~"))
                                            {
                                                MMR = rankText[i + 3];
                                            }
                                            else
                                            {
                                                MMR = rankText[i + 2];
                                            }
                                            //GamesPlayed = rankText[i + 5];
                                            GamesPlayed = "N/A";
                                            //Percentage = $"Top {rankText[i + 5].Replace('%', ' ').Replace(')', ' ').Trim()}%";
                                            Percentage = "N/A";
                                            divisionFound = true;
                                        }
                                    }
                                    while (divisionFound == false);

                                    stats = await AddStats(stats, Title, Rank, Division, MMR, fullURL);
                                }
                            }                            
                        }

                        if (nodeText.ToLower().Contains("solo") && nodeText.ToLower().Contains("top"))
                        {
                            oi++;
                            bool placementMatches = false;
                            var rankText = nodeText.Trim().Split(' ').ToList();
                            if (rankText.Count() > 4)
                            {
                                rankText.ForEach(r =>
                                {
                                    if (r.Trim() == "100%)")
                                    {
                                        stats.Add(new RocketLeagueStats
                                        {
                                            GamesPlayed = "N/A",
                                            Title = "N/A",
                                            Rank = "N/A",
                                            Division = "N/A",
                                            MMR = 0,
                                            Percentage = "N/A"
                                        });

                                        placementMatches = true;
                                    }
                                    else
                                    {
                                        //Console.WriteLine(r);
                                    }
                                });

                                if (!placementMatches)
                                {
                                    int textCount = rankText.Count() - 6;
                                    string Title = $"{rankText[1]} {rankText[2]} {rankText[3]}";
                                    string Rank = string.Empty;
                                    string Division = string.Empty;
                                    string Percentage = string.Empty;
                                    string MMR = string.Empty;
                                    string GamesPlayed = string.Empty;
                                    int i = 4;
                                    bool divisionFound = false;
                                    do
                                    {
                                        if (rankText[i].ToLower() != "division")
                                        {
                                            Rank += $"{rankText[i]} ";
                                            i++;
                                        }
                                        else
                                        {
                                            Division = rankText[i + 1];
                                            if (rankText[i + 2].Contains("~"))
                                            {
                                                MMR = rankText[i + 3];
                                            }
                                            else
                                            {
                                                MMR = rankText[i + 2];
                                            }
                                            //GamesPlayed = rankText[i + 5];
                                            GamesPlayed = "N/A";
                                            //Percentage = $"Top {rankText[i + 5].Replace('%', ' ').Replace(')', ' ').Trim()}%";
                                            Percentage = "N/A";
                                            divisionFound = true;
                                        }
                                    }
                                    while (divisionFound == false);

                                    stats = await AddStats(stats, Title, Rank, Division, MMR, fullURL);
                                }
                            }                            
                        }

                        if (nodeText.ToLower().Contains("doubles") && nodeText.ToLower().Contains("top"))
                        {
                            oi++;
                            bool placementMatches = false;
                            var rankText = nodeText.Trim().Split(' ').ToList();
                            if (rankText.Count() > 4)
                            {
                                rankText.ForEach(r =>
                                {
                                    if (r.Trim() == "100%)")
                                    {
                                        stats.Add(new RocketLeagueStats
                                        {
                                            GamesPlayed = "N/A",
                                            Title = "N/A",
                                            Rank = "N/A",
                                            Division = "N/A",
                                            MMR = 0,
                                            Percentage = "N/A"
                                        });

                                        placementMatches = true;
                                    }
                                    else
                                    {
                                        //Console.WriteLine(r);
                                    }
                                });

                                if (!placementMatches)
                                {
                                    int textCount = rankText.Count() - 6;
                                    string Title = $"{rankText[1]} {rankText[2]}";
                                    string Rank = string.Empty;
                                    string Division = string.Empty;
                                    string Percentage = string.Empty;
                                    string MMR = string.Empty;
                                    string GamesPlayed = string.Empty;
                                    int i = 3;
                                    bool divisionFound = false;
                                    do
                                    {
                                        if (rankText[i].ToLower() != "division")
                                        {
                                            Rank += $"{rankText[i]} ";
                                            i++;
                                        }
                                        else
                                        {
                                            Division = rankText[i + 1];
                                            if (rankText[i + 2].Contains("~"))
                                            {
                                                MMR = rankText[i + 3];
                                            }
                                            else
                                            {
                                                MMR = rankText[i + 2];
                                            }
                                            //GamesPlayed = rankText[i + 5];
                                            GamesPlayed = "N/A";
                                            //Percentage = $"Top {rankText[i + 5].Replace('%', ' ').Replace(')', ' ').Trim()}%";
                                            Percentage = "N/A";
                                            divisionFound = true;
                                        }
                                    }
                                    while (divisionFound == false);

                                    stats = await AddStats(stats, Title, Rank, Division, MMR, fullURL);
                                }
                            }                           
                        }
                        if (nodeText.ToLower().Contains("unranked") && nodeText.ToLower().Contains("top"))
                        {
                            oi++;
                            bool placementMatches = false;
                            var rankText = nodeText.Trim().Split(' ').ToList();
                            if (rankText.Count() > 4)
                            {
                                rankText.ForEach(r =>
                                {
                                    if (r.Trim() == "100%)")
                                    {
                                        stats.Add(new RocketLeagueStats
                                        {
                                            GamesPlayed = "N/A",
                                            Title = "N/A",
                                            Rank = "N/A",
                                            Division = "N/A",
                                            MMR = 0,
                                            Percentage = "N/A"
                                        });

                                        placementMatches = true;
                                    }
                                    else
                                    {
                                        //Console.WriteLine(r);
                                    }
                                });
                                if (!placementMatches)
                                {
                                    int textCount = rankText.Count() - 6;
                                    string Title = $"{rankText[1]}";
                                    string Rank = string.Empty;
                                    string Division = string.Empty;
                                    string Percentage = string.Empty;
                                    string MMR = string.Empty;
                                    string GamesPlayed = string.Empty;
                                    Rank = "Forever Noob ";
                                    Division = rankText[3];
                                    MMR = rankText[4];
                                    Percentage = "N/A";
                                    //Percentage = $"Top {rankText[6].Replace('%', ' ').Replace(')', ' ').Trim()}%";

                                    stats = await AddStats(stats, Title, Rank, Division, MMR, fullURL);
                                }
                            }
                        }                            
                    }
                }
            }

            return stats;            
        }

        public async Task<List<RocketLeagueStats>> getRLStats(string rlUserName, bool ps)
        {
            string statsPage = "https://rocketleague.tracker.network/profile/ps/";
            string fullURL = statsPage + rlUserName;
            string foundText = string.Empty;
            string pattern = "\\s+";
            string replacement = " ";
            StringBuilder sb = new StringBuilder();
            Regex rgx = new Regex(pattern);            
            var document = new HtmlDocument();
            string url_string = string.Empty;
            var stats = new List<RocketLeagueStats>();
            bool seasonEnderFound = false;

            using (var httpclient = new HttpClient())
            {
                url_string = await httpclient.GetStringAsync(fullURL);
            }            

            document.LoadHtml(url_string);

            var nodes = document.DocumentNode.Descendants("tr");

            sb.AppendLine();
            int oi = 0;

            foreach (var node in nodes.Skip(1))
            {
                if (!seasonEnderFound)
                {
                    if (oi == 4)
                    {
                        seasonEnderFound = true;
                    }
                    if (!String.IsNullOrWhiteSpace(node.InnerText))
                    {
                        string nodeText = node.InnerText;
                        nodeText = rgx.Replace(nodeText, replacement);
                        if (nodeText.ToLower().Contains("standard") && !(nodeText.ToLower().Contains("solo")))
                        {
                            oi++;
                            bool placementMatches = false;
                            var rankText = nodeText.Trim().Split(' ').ToList();
                            rankText.ForEach(r =>
                            {
                                if (r.Trim() == "100%)")
                                {
                                    stats.Add(new RocketLeagueStats
                                    {
                                        GamesPlayed = "N/A",
                                        Title = "N/A",
                                        Rank = "N/A",
                                        Division = "N/A",
                                        MMR = 0,
                                        Percentage = "N/A"
                                    });

                                    placementMatches = true;
                                }
                                else
                                {
                                    //Console.WriteLine(r);
                                }
                            });

                            if (!placementMatches)
                            {
                                int textCount = rankText.Count() - 6;
                                string Title = $"{rankText[1]} {rankText[2]}";
                                string Rank = string.Empty;
                                string Division = string.Empty;
                                string Percentage = string.Empty;
                                string MMR = string.Empty;
                                string GamesPlayed = string.Empty;
                                int i = 3;
                                bool divisionFound = false;

                                do
                                {
                                    if (rankText[i].ToLower() != "division")
                                    {
                                        Rank += $"{rankText[i]} ";
                                        i++;
                                    }
                                    else
                                    {
                                        Division = rankText[i + 1];
                                        if (rankText[i + 2].Contains("~"))
                                        {
                                            MMR = rankText[i + 3];
                                        }
                                        else
                                        {
                                            MMR = rankText[i + 2];
                                        }
                                        //GamesPlayed = rankText[i + 5];
                                        GamesPlayed = "N/A";
                                        //Percentage = $"Top {rankText[i + 5].Replace('%', ' ').Replace(')', ' ').Trim()}%";
                                        Percentage = "N/A";
                                        divisionFound = true;
                                    }
                                }
                                while (divisionFound == false);

                                stats = await AddStats(stats, Title, Rank, Division, MMR, fullURL);

                            }
                        }

                        if (nodeText.ToLower().Contains("duel"))
                        {
                            oi++;
                            bool placementMatches = false;
                            var rankText = nodeText.Trim().Split(' ').ToList();
                            rankText.ForEach(r =>
                            {
                                if (r.Trim() == "100%)")
                                {
                                    stats.Add(new RocketLeagueStats
                                    {
                                        GamesPlayed = "N/A",
                                        Title = "N/A",
                                        Rank = "N/A",
                                        Division = "N/A",
                                        MMR = 0,
                                        Percentage = "N/A"
                                    });

                                    placementMatches = true;
                                }
                                else
                                {
                                    //Console.WriteLine(r);
                                }
                            });

                            if (!placementMatches)
                            {
                                int textCount = rankText.Count() - 6;
                                string Title = $"{rankText[1]} {rankText[2]}";
                                string Rank = string.Empty;
                                string Division = string.Empty;
                                string Percentage = string.Empty;
                                string MMR = string.Empty;
                                string GamesPlayed = string.Empty;
                                int i = 3;
                                bool divisionFound = false;
                                do
                                {
                                    if (rankText[i].ToLower() != "division")
                                    {
                                        Rank += $"{rankText[i]} ";
                                        i++;
                                    }
                                    else
                                    {
                                        Division = rankText[i + 1];
                                        if (rankText[i + 2].Contains("~"))
                                        {
                                            MMR = rankText[i + 3];
                                        }
                                        else
                                        {
                                            MMR = rankText[i + 2];
                                        }
                                        //GamesPlayed = rankText[i + 5];
                                        GamesPlayed = "N/A";
                                        //Percentage = $"Top {rankText[i + 5].Replace('%', ' ').Replace(')', ' ').Trim()}%";
                                        Percentage = "N/A";
                                        divisionFound = true;
                                    }
                                }
                                while (divisionFound == false);

                                stats = await AddStats(stats, Title, Rank, Division, MMR, fullURL);
                            }
                        }

                        if (nodeText.ToLower().Contains("solo"))
                        {
                            oi++;
                            bool placementMatches = false;
                            var rankText = nodeText.Trim().Split(' ').ToList();
                            rankText.ForEach(r =>
                            {
                                if (r.Trim() == "100%)")
                                {
                                    stats.Add(new RocketLeagueStats
                                    {
                                        GamesPlayed = "N/A",
                                        Title = "N/A",
                                        Rank = "N/A",
                                        Division = "N/A",
                                        MMR = 0,
                                        Percentage = "N/A"
                                    });

                                    placementMatches = true;
                                }
                                else
                                {
                                    //Console.WriteLine(r);
                                }
                            });

                            if (!placementMatches)
                            {
                                int textCount = rankText.Count() - 6;
                                string Title = $"{rankText[1]} {rankText[2]} {rankText[3]}";
                                string Rank = string.Empty;
                                string Division = string.Empty;
                                string Percentage = string.Empty;
                                string MMR = string.Empty;
                                string GamesPlayed = string.Empty;
                                int i = 4;
                                bool divisionFound = false;
                                do
                                {
                                    if (rankText[i].ToLower() != "division")
                                    {
                                        Rank += $"{rankText[i]} ";
                                        i++;
                                    }
                                    else
                                    {
                                        Division = rankText[i + 1];
                                        if (rankText[i + 2].Contains("~"))
                                        {
                                            MMR = rankText[i + 3];
                                        }
                                        else
                                        {
                                            MMR = rankText[i + 2];
                                        }
                                        //GamesPlayed = rankText[i + 5];
                                        GamesPlayed = "N/A";
                                        //Percentage = $"Top {rankText[i + 5].Replace('%', ' ').Replace(')', ' ').Trim()}%";
                                        Percentage = "N/A";
                                        divisionFound = true;
                                    }
                                }
                                while (divisionFound == false);

                                stats = await AddStats(stats, Title, Rank, Division, MMR, fullURL);
                            }
                        }

                        if (nodeText.ToLower().Contains("doubles"))
                        {
                            oi++;
                            bool placementMatches = false;
                            var rankText = nodeText.Trim().Split(' ').ToList();
                            rankText.ForEach(r =>
                            {
                                if (r.Trim() == "100%)")
                                {
                                    stats.Add(new RocketLeagueStats
                                    {
                                        GamesPlayed = "N/A",
                                        Title = "N/A",
                                        Rank = "N/A",
                                        Division = "N/A",
                                        MMR = 0,
                                        Percentage = "N/A"
                                    });

                                    placementMatches = true;
                                }
                                else
                                {
                                    //Console.WriteLine(r);
                                }
                            });

                            if (!placementMatches)
                            {
                                int textCount = rankText.Count() - 6;
                                string Title = $"{rankText[1]} {rankText[2]}";
                                string Rank = string.Empty;
                                string Division = string.Empty;
                                string Percentage = string.Empty;
                                string MMR = string.Empty;
                                string GamesPlayed = string.Empty;
                                int i = 3;
                                bool divisionFound = false;
                                do
                                {
                                    if (rankText[i].ToLower() != "division")
                                    {
                                        Rank += $"{rankText[i]} ";
                                        i++;
                                    }
                                    else
                                    {
                                        Division = rankText[i + 1];
                                        if (rankText[i + 2].Contains("~"))
                                        {
                                            MMR = rankText[i + 3];
                                        }
                                        else
                                        {
                                            MMR = rankText[i + 2];
                                        }
                                        //GamesPlayed = rankText[i + 5];
                                        GamesPlayed = "N/A";
                                        //Percentage = $"Top {rankText[i + 5].Replace('%', ' ').Replace(')', ' ').Trim()}%";
                                        Percentage = "N/A";
                                        divisionFound = true;
                                    }
                                }
                                while (divisionFound == false);

                                stats = await AddStats(stats, Title, Rank, Division, MMR, fullURL);
                            }
                        }
                        if (nodeText.ToLower().Contains("unranked"))
                        {
                            oi++;
                            bool placementMatches = false;
                            var rankText = nodeText.Trim().Split(' ').ToList();
                            rankText.ForEach(r =>
                            {
                                if (r.Trim() == "100%)")
                                {
                                    stats.Add(new RocketLeagueStats
                                    {
                                        GamesPlayed = "N/A",
                                        Title = "N/A",
                                        Rank = "N/A",
                                        Division = "N/A",
                                        MMR = 0,
                                        Percentage = "N/A"
                                    });

                                    placementMatches = true;
                                }
                                else
                                {
                                    //Console.WriteLine(r);
                                }
                            });

                            if (!placementMatches)
                            {
                                int textCount = rankText.Count() - 6;
                                string Title = $"{rankText[1]}";
                                string Rank = string.Empty;
                                string Division = string.Empty;
                                string Percentage = string.Empty;
                                string MMR = string.Empty;
                                string GamesPlayed = string.Empty;
                                Rank = "Forever Noob ";
                                Division = rankText[3];
                                MMR = rankText[4];
                                Percentage = "N/A";
                                //Percentage = $"Top {rankText[6].Replace('%', ' ').Replace(')', ' ').Trim()}%";

                                stats = await AddStats(stats, Title, Rank, Division, MMR, fullURL);
                            }
                        }
                    }
                }
            }

            return stats;
        }

        private async Task<List<RocketLeagueStats>> AddStats(List<RocketLeagueStats> stats, string Title, string Rank, string Division, string MMR, string fullUrl)
        {
            stats.Add(new RocketLeagueStats
            {
                //GamesPlayed = GamesPlayed,
                Title = Title,
                Rank = Rank,
                Division = Division,
                MMR = int.Parse(MMR.Replace(",","")),
                FromURL = fullUrl
                //Percentage = Percentage
            });
            return stats;
        }

        //public string getRLStats(bool findName, string discordName)
        //{
        //    string rlUserName = string.Empty;

        //    if (findName)
        //    {
        //        try
        //        {
        //            rlUserName = getUserName(discordName);
        //        }
        //        catch
        //        {
        //            Console.WriteLine($"Error looking up {discordName}!");
        //            string returnString = null;
        //            return returnString;
        //        }
        //    }

        //    string statsPage = "https://rocketleague.tracker.network/profile/steam/";
        //    string fullURL = statsPage + rlUserName;
        //    string foundText = string.Empty;
        //    string pattern = "\\s+";
        //    string replacement = " ";
        //    string[] findMe = new string[] { "n/a" };

        //    Regex rgx = new Regex(pattern);
        //    HtmlWeb web = new HtmlWeb();
        //    HtmlDocument document = web.Load(fullURL);

        //    foundText = $"__Rocket League Stats For: **{rlUserName}**\n\n__";

        //    foreach (HtmlNode node in document.DocumentNode.SelectNodes("//td"))
        //    {
        //        string buildText = string.Empty;

        //        if (!String.IsNullOrWhiteSpace(node.InnerText))
        //        {
        //            string nodeText = node.InnerText;

        //            nodeText = rgx.Replace(nodeText, replacement);

        //            if (nodeText.Contains("Division"))
        //            {
        //                string[] splitText = nodeText.Split(' ');

        //                foreach (string text in splitText)
        //                {
        //                    switch (text.ToLower())
        //                    {
        //                        case "division":
        //                            {
        //                                buildText += "\n" + text + " ";
        //                                break;
        //                            }
        //                        default:
        //                            {
        //                                buildText += text + " ";
        //                                break;
        //                            }
        //                    }
        //                }

        //                foundText += buildText.Trim() + "\n";

        //            }

        //            if (nodeText.ToLower().Contains("top"))
        //            {

        //                foundText += nodeText.Trim() + "\n\n";

        //            }
        //        };
        //    }

        //    return foundText;

        //}

        //public string getUserName(string discordUser)
        //{
        //    string userName = string.Empty;
        //    string[] userNameList = null;

        //    try
        //    {
        //        userNameList = File.ReadAllLines(path);
        //        if (string.IsNullOrEmpty(userNameList.ToString()))
        //        {
        //            return "no users found!";
        //        }
        //        else
        //        {
        //            foreach (string name in userNameList)
        //            {
        //                if (name.Split(',')[0] == discordUser)
        //                {
        //                    userName = name.Split(',')[1];
        //                    break;
        //                }
        //            }
        //        }
        //    }
        //    catch (FileNotFoundException ex)
        //    {
        //        Console.WriteLine($"File not found {path}");
        //    }


        //    return userName;
        //}

        //public void setUserName(string discordUser, string userToSet)
        //{
        //    List<String> userNameList = new List<String>();
        //    bool foundInList = false;
        //    string removeUser = string.Empty;
        //    string addUser = string.Empty;
        //    string[] fileImport = File.ReadAllLines(path);
        //    //url paste parse name
        //    addUser = discordUser + "," + userToSet;

        //    foreach (string file in fileImport)
        //    {
        //        userNameList.Add(file);
        //    }

        //    foreach (string user in userNameList)
        //    {
        //        if (user.Split(',')[0] == discordUser)
        //        {
        //            removeUser = user;
        //            foundInList = true;
        //        }
        //    }

        //    if (foundInList)
        //    {
        //        userNameList.Remove(removeUser);
        //    }

        //    userNameList.Add(addUser);

        //    File.WriteAllLines(path, userNameList);

        //}
    }
}
