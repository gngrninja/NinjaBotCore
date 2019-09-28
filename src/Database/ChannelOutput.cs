using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class ChannelOutput
    {
        [Key]
        public long Id { get; set; }
        public string ServerName { get; set; }
        public Nullable<long> ServerId { get; set; }
        public string ChannelName { get; set; }
        public Nullable<long> ChannelId { get; set; }
        public string SetByName { get; set; }
        public Nullable<long> SetById { get; set; }
        public Nullable<System.DateTime> SetTime { get; set; }
    }
}