using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class AuctionItemMapping
    {
        [Key]
        public long MapId { get; set; }
        public Nullable<long> ItemId { get; set; }
        public string Name { get; set; }
    }
}