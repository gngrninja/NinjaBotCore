using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class AwaySystem
    {
        [Key]
        public long AwayId { get; set; }
        public string UserName { get; set; }
        public string Message { get; set; }
        public Nullable<bool> Status { get; set; }
        public Nullable<System.DateTime> TimeAway { get; set; }
    }
}