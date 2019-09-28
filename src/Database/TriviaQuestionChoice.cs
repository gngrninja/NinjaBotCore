using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class TriviaQuestionChoice
    {
        [Key]
        public long ChoiceId { get; set; }
        public Nullable<long> QuestionId { get; set; }
        public Nullable<bool> IsRightChoice { get; set; }
        public string Choice { get; set; }

        public virtual TriviaQuestion TriviaQuestion { get; set; }
    }
}