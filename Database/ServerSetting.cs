using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class ServerSetting
    {
        [Key]
        public long Id { get; set; }
        public string ServerName { get; set; }
        public Nullable<long> ServerId { get; set; }
        public Nullable<bool> Announcements { get; set; }
        public Nullable<bool> OutputChannel { get; set; }
    }
}