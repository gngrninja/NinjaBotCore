#nullable enable

using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class UserPushGroupSettings
    {
        [Key]
        public long UserId { get; set; }

        public bool DmOnGroupFull { get; set; } = false;

        public bool DmOnRosterPing { get; set; } = true;

        public DateTime? UpdatedAt { get; set; }
    }
}
