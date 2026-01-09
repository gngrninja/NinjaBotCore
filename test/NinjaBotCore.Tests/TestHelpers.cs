using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Shared test utilities and configuration helpers
    /// </summary>
    public static class TestHelpers
    {
        /// <summary>
        /// Configures an in-memory database for testing with proper warning suppression
        /// to avoid "ManyServiceProvidersCreatedWarning" when running multiple test classes
        /// </summary>
        public static DbContextOptionsBuilder ConfigureTestDatabase(
            this DbContextOptionsBuilder builder,
            string databaseName)
        {
            return builder
                .UseInMemoryDatabase(databaseName)
                .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .EnableSensitiveDataLogging();
        }

        /// <summary>
        /// Adds a test-configured NinjaBotEntities to the service collection
        /// </summary>
        public static IServiceCollection AddTestDbContext(
            this IServiceCollection services,
            string databaseName)
        {
            return services.AddDbContext<NinjaBotEntities>(options =>
                options.ConfigureTestDatabase(databaseName));
        }
    }
}
