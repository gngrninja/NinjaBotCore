using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NinjaBotCore.Database
{
public class NinjaBotEntitiesFactory : IDesignTimeDbContextFactory<NinjaBotEntities>
    {
        public NinjaBotEntities CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();
            optionsBuilder.UseSqlite("Data Source=ninjabot.db");

            return new NinjaBotEntities(optionsBuilder.Options);
        }
    }
}