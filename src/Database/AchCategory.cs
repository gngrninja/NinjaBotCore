using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{    
    public partial class AchCategory
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
        public AchCategory()
        {
            this.FindWowCheeves = new HashSet<FindWowCheeve>();
        }
        
        [Key]
        public long CatId { get; set; }
        public string CatName { get; set; }
    
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public virtual ICollection<FindWowCheeve> FindWowCheeves { get; set; }
    }
}