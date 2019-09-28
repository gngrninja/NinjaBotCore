using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class Request
    {
        [Key]
        public long Id { get; set; }
        public long UserId { get; set; }
        public string UserName { get; set; }
        public long ChannelId { get; set; }
        public string ChannelName { get; set; }
        public string Command { get; set; }
        public string Parameters { get; set; }
        public string ServerName { get; set; }
        public long ServerID { get; set; }
        public Nullable<System.DateTime> RequestTime { get; set; }
        public Nullable<bool> Success { get; set; }
        public string FailureReason { get; set; }
    }
}