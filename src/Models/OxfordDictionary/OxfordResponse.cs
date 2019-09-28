namespace NinjaBotCore.Models.OxfordDictionary 
{
    public class OxfordResponses
    {
        public class OxfordSearch
        {
            public Result[] results { get; set; }
            public Metadata metadata { get; set; }
        }

        public class Metadata
        {
            public int offset { get; set; }
            public int limit { get; set; }
            public string sourceLanguage { get; set; }
            public string provider { get; set; }
            public int total { get; set; }
        }

        public class Result
        {
            public string inflection_id { get; set; }
            public string matchString { get; set; }
            public string id { get; set; }
            public string region { get; set; }
            public string matchType { get; set; }
            public string word { get; set; }
        }


        public class OxfordDefinition
        {
            public MetadataDefine metadata { get; set; }
            public DefineResult[] results { get; set; }
        }

        public class MetadataDefine
        {
            public string provider { get; set; }
        }

        public class DefineResult
        {
            public string id { get; set; }
            public string language { get; set; }
            public Lexicalentry[] lexicalEntries { get; set; }
            public string type { get; set; }
            public string word { get; set; }
        }

        public class Lexicalentry
        {
            public Entry[] entries { get; set; }
            public string language { get; set; }
            public string lexicalCategory { get; set; }
            public DeriviativeOf[] derivativeOf { get; set; }
            public Pronunciation[] pronunciations { get; set; }
            public string text { get; set; }
        }
        public class DeriviativeOf
        {
            public string id { get; set; }
            public string text { get; set; }
        }

        public class Entry
        {
            public string[] etymologies { get; set; }
            public Grammaticalfeature[] grammaticalFeatures { get; set; }
            public string homographNumber { get; set; }
            public Note[] notes { get; set; }
            public Sens[] senses { get; set; }
        }

        public class Grammaticalfeature
        {
            public string text { get; set; }
            public string type { get; set; }
        }

        public class Note
        {
            public string text { get; set; }
            public string type { get; set; }
        }

        public class Sens
        {
            public string[] definitions { get; set; }
            public string[] domains { get; set; }
            public string[] crossReferenceMarkers { get; set; }
            public Crossreference[] crossReferences { get; set; }
            public Example[] examples { get; set; }
            public string id { get; set; }
            public Subsens[] subsenses { get; set; }
            public string[] regions { get; set; }
        }

        public class Example
        {
            public string text { get; set; }
        }

        public class Subsens
        {
            public string[] definitions { get; set; }
            public string[] domains { get; set; }
            public string id { get; set; }
            public Example1[] examples { get; set; }
            public Crossreference[] crossReferences { get; set; }
            public Variantform[] variantForms { get; set; }
            public Note1[] notes { get; set; }
            public string[] registers { get; set; }
        }

        public class Example1
        {
            public string text { get; set; }
        }

        public class Crossreference
        {
            public string id { get; set; }
            public string text { get; set; }
            public string type { get; set; }
        }

        public class Variantform
        {
            public string text { get; set; }
        }

        public class Note1
        {
            public string text { get; set; }
            public string type { get; set; }
        }

        public class Pronunciation
        {
            public string audioFile { get; set; }
            public string[] dialects { get; set; }
            public string phoneticNotation { get; set; }
            public string phoneticSpelling { get; set; }
        }
    }
}