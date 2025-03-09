using System.ComponentModel.DataAnnotations;
using System;

namespace NinjaBotCore.Database
{
    public partial class WclPosted
    {
        [Key]
        public long Id { get; set; }
        public long ServerId { get; set; }
        public long ChannelId { get; set; }
        public string ChannelName { get; set; }
        public string ServerName { get; set; }
        public string ReportId { get; set; }
    }
}