using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class ServerGreeting
    {
        [Key]
        public long DiscordGuildId { get; set; }
        public Nullable<bool> GreetUsers { get; set; }
        public string Greeting { get; set; }
        public Nullable<long> SetById { get; set; }
        public string SetByName { get; set; }
        public Nullable<System.DateTime> TimeSet { get; set; }
        public string PartingMessage { get; set; }
        public Nullable<long> GreetingChannelId { get; set; }
        public Nullable<long> PartingChannelId { get; set; }
        public string GreetingChannelName { get; set; }
    }
}