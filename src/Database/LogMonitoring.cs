using System.ComponentModel.DataAnnotations;
using System;

namespace NinjaBotCore.Database
{
    public partial class LogMonitoring
    {
        [Key]
        public long Id { get; set; }
        public long ServerId { get; set; }
        public long ChannelId { get; set; }
        public string ChannelName { get; set; }
        public string ServerName { get; set; }
        public bool MonitorLogs { get; set; }
        public bool WatchLog { get; set; }
        public string RetailReportId { get; set; }
        public string ClassicReportId { get; set; }
        public string VanillaReportId { get; set; }
        public string ReportId { get; set; }
        public DateTime? LatestLog { get; set; }
        public DateTime? LatestLogClassic { get; set; }
        public DateTime? LatestLogVanilla { get; set; }
        public DateTime? LatestLogRetail { get; set; }

        /// <summary>
        /// When we last checked WCL for Retail logs (for tiered checking)
        /// </summary>
        public DateTime? LastCheckedRetail { get; set; }

        /// <summary>
        /// When we last checked WCL for Classic logs (for tiered checking)
        /// </summary>
        public DateTime? LastCheckedClassic { get; set; }

        /// <summary>
        /// When we last checked WCL for Vanilla logs (for tiered checking)
        /// </summary>
        public DateTime? LastCheckedVanilla { get; set; }

        /// <summary>The newest Forever report posted for this server.</summary>
        public string ForeverReportId { get; set; }

        /// <summary>When a Forever log was last posted, or when watching was turned on. Decides how often the guild is checked.</summary>
        public DateTime? LatestLogForever { get; set; }

        /// <summary>
        /// When we last checked WCL for Forever logs (for tiered checking)
        /// </summary>
        public DateTime? LastCheckedForever { get; set; }

        /// <summary>Who turned log watching on, so they can be told when posts stop working.</summary>
        public long? SetById { get; set; }

        [MaxLength(100)]
        public string SetByName { get; set; }
    }
}