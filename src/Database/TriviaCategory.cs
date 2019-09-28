using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class TriviaCategory
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
        public TriviaCategory()
        {
            this.TriviaQuestions = new HashSet<TriviaQuestion>();
        }
    
        public string Name { get; set; }
        [Key]
        public long CategoryId { get; set; }
    
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public virtual ICollection<TriviaQuestion> TriviaQuestions { get; set; }
    }
}