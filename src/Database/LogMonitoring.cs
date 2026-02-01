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
    }
}