using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NinjaBotCore.Database;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Away
{
    public class AwayData
    {
        public void setAwayUser(AwaySystem awayInfo)
        {

            var awayUser = new AwaySystem();

            using (var db = new NinjaBotEntities())
            {
                awayUser = db.AwaySystem.Where(a => a.UserName == awayInfo.UserName).FirstOrDefault();
                if (awayUser == null)
                {
                    db.AwaySystem.Add(awayInfo);
                }
                else
                {
                    awayUser.Status = awayInfo.Status;
                    awayUser.Message = awayInfo.Message;
                    awayUser.TimeAway = awayInfo.TimeAway;
                }
                db.SaveChanges();
            }
        }

        public AwaySystem getAwayUser(string discordUserName)
        {
            var awayUser = new AwaySystem();
            using (var db = new NinjaBotEntities())
            {
                awayUser = db.AwaySystem.Where(a => a.UserName == discordUserName).FirstOrDefault();
            }
            return awayUser;
        }
    }
}
