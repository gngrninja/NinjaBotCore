using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class WowResources
    {
        [Key]
        public long Id { get; set; }
        public Nullable<long> ServerId { get; set; }
        public string ClassName { get; set; }
        public string Specialization { get; set; }
        public string Resource { get; set; }
        public string ResourceDescription { get; set; }                
    }
}