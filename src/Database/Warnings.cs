using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class Warnings
    {
        [Key]
        public long Warnid { get; set; }
        public long ServerId { get; set; }
        public string ServerName { get; set; }                
        public long UserWarnedId { get; set; }
        public string UserWarnedName { get; set; }        
        public long IssuerId { get; set; }
        public string IssuerName { get; set; }        
        public Nullable<System.DateTime> TimeIssued { get; set; }
        public int NumWarnings { get; set;}        
    }
}