using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class WowAuctionPrice
    {
        [Key]
        public long AuctionPriceId { get; set; }
        public Nullable<long> AuctionItemId { get; set; }
        public string AuctionRealm { get; set; }
        public Nullable<long> MinPrice { get; set; }
        public Nullable<long> AvgPrice { get; set; }
        public Nullable<long> MaxPrice { get; set; }
        public Nullable<long> Seen { get; set; }
    }
}