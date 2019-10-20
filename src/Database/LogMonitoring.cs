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
        public DateTime LatestLogClassic { get; set; }
        public DateTime LatestLogRetail { get; set; }
    }
}