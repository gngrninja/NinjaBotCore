using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Models.Wow
{
    public class WowRealmSearch
    {
        public class Category
        {
            public string it_IT { get; set; }
            public string ru_RU { get; set; }
            public string en_GB { get; set; }
            public string zh_TW { get; set; }
            public string ko_KR { get; set; }
            public string en_US { get; set; }
            public string es_MX { get; set; }
            public string pt_BR { get; set; }
            public string es_ES { get; set; }
            public string zh_CN { get; set; }
            public string fr_FR { get; set; }
            public string de_DE { get; set; }
        }

        public class Data
        {
            public bool is_tournament { get; set; }
            public string timezone { get; set; }
            public Name name { get; set; }
            public int id { get; set; }
            public Region region { get; set; }
            public Category category { get; set; }
            public string locale { get; set; }
            public Type type { get; set; }
            public string slug { get; set; }
        }

        public class Key
        {
            public string href { get; set; }
        }

        public class Name
        {
            public string it_IT { get; set; }
            public string ru_RU { get; set; }
            public string en_GB { get; set; }
            public string zh_TW { get; set; }
            public string ko_KR { get; set; }
            public string en_US { get; set; }
            public string es_MX { get; set; }
            public string pt_BR { get; set; }
            public string es_ES { get; set; }
            public string zh_CN { get; set; }
            public string fr_FR { get; set; }
            public string de_DE { get; set; }
        }

        public class Region
        {
            public Name name { get; set; }
            public int id { get; set; }
        }

        public class Result
        {
            public Key key { get; set; }
            public Data data { get; set; }
        }

        public class Root
        {
            public int page { get; set; }
            public int pageSize { get; set; }
            public int maxPageSize { get; set; }
            public int pageCount { get; set; }
            public List<Result> results { get; set; }
        }

        public class Type
        {
            public Name name { get; set; }
            public string type { get; set; }
        }
    }
}
