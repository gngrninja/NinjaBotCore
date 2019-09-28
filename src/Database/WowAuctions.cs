using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class WowAuctions
    {
        [Key]
        public long AuctionId { get; set; }
        public string RealmName { get; set; }
        public string RealmSlug { get; set; }
        public Nullable<long> WowAuctionId { get; set; }
        public Nullable<long> AuctionItemId { get; set; }
        public string AuctionOwner { get; set; }
        public string AuctionOwnerRealm { get; set; }
        public Nullable<long> AuctionBid { get; set; }
        public Nullable<long> AuctionBuyout { get; set; }
        public Nullable<long> AuctionQuantity { get; set; }
        public string AuctionTimeLeft { get; set; }
        public Nullable<long> AuctionRand { get; set; }
        public Nullable<long> AuctionSeed { get; set; }
        public Nullable<long> AuctionContext { get; set; }
        public Nullable<System.DateTime> DateModified { get; set; }
    }
}