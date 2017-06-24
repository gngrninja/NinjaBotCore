using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class TriviaQuestion
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2214:DoNotCallOverridableMethodsInConstructors")]
        public TriviaQuestion()
        {
            this.TriviaQuestionChoices = new HashSet<TriviaQuestionChoice>();
        }
        [Key]
        public long QuestionId { get; set; }
        public string Question { get; set; }
        public Nullable<bool> IsActive { get; set; }
        public Nullable<long> Category { get; set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly")]
        public virtual ICollection<TriviaQuestionChoice> TriviaQuestionChoices { get; set; }
        public virtual TriviaCategory TriviaCategory { get; set; }
    }
}