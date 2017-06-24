using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public partial class C8Ball
    {
        [Key]
        public long AnswerId { get; set; }
        public string Answer { get; set; }
        public string Color { get; set; }
    }
}