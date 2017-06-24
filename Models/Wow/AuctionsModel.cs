using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Models.Wow
{
    public class AuctionsModel
    {
        public class AuctionFile
        {
            public File[] files { get; set; }
        }

        public class File
        {
            public string url { get; set; }
            public long lastModified { get; set; }
        }

        public class Auctions
        {
            public Realm[] realms { get; set; }
            public Auction[] auctions { get; set; }
        }

        public class Realm
        {
            public string name { get; set; }
            public string slug { get; set; }
        }

        public class Auction
        {
            public DateTime fileDate { get; set; }
            public int auc { get; set; }
            public int item { get; set; }
            public string owner { get; set; }
            public string ownerRealm { get; set; }
            public long bid { get; set; }
            public long buyout { get; set; }
            public int quantity { get; set; }
            public string timeLeft { get; set; }
            public int rand { get; set; }
            public long seed { get; set; }
            public int context { get; set; }
            public Bonuslist[] bonusLists { get; set; }
            public Modifier[] modifiers { get; set; }
            public int petSpeciesId { get; set; }
            public int petBreedId { get; set; }
            public int petLevel { get; set; }
            public int petQualityId { get; set; }
        }

        public class Bonuslist
        {
            public int bonusListId { get; set; }
        }

        public class Modifier
        {
            public int type { get; set; }
            public int value { get; set; }
        }
    }
}
