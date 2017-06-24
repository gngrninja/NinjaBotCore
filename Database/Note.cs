using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class Note
    {
        [Key]
        public long Id { get; set; }
        public string Note1 { get; set; }
        public string ServerName { get; set; }
        public Nullable<long> ServerId { get; set; }
        public string SetBy { get; set; }
        public Nullable<long> SetById { get; set; }
        public Nullable<System.DateTime> TimeSet { get; set; }
    }
}