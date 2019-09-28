using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class QuestionAnswer
    {
        [Key]
        public long Id { get; set; }
        public string DiscordUserName { get; set; }
        public Nullable<long> QuestionId { get; set; }
        public Nullable<long> ChoiceId { get; set; }
        public Nullable<bool> IsRight { get; set; }
        public Nullable<System.DateTime> AnswerTime { get; set; }
        public string test { get; set; }
    }
}