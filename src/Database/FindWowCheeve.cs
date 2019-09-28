using System.Collections.Generic;
using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class FindWowCheeve
    {
        [Key]
        public long AchId { get; set; }        
        public virtual AchCategory AchCategory { get; set; }
    }
}