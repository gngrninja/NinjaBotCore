#nullable enable
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace NinjaBotCore.Database
{
    public static class DatabaseConfigurator
    {
        private static Action<DbContextOptionsBuilder>? _configureBuilder;

        public static void ConfigureFrom(IConfiguration configuration)
        {
            _configureBuilder = builder =>
            {
                var provider = configuration["Database:Provider"];
                var connectionString = configuration.GetConnectionString("NinjaBot");

                if (string.IsNullOrWhiteSpace(provider))
                {
                    ConfigureSqlite(builder, connectionString);
                    return;
                }

                switch (provider.Trim().ToLowerInvariant())
                {
                    case "postgres":
                    case "postgresql":
                        ConfigurePostgres(builder, connectionString);
                        break;
                    case "sqlite":
                        ConfigureSqlite(builder, connectionString);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported database provider '{provider}'.");
                }
            };
        }

        public static void Apply(DbContextOptionsBuilder builder)
        {
            if (_configureBuilder != null)
            {
                _configureBuilder(builder);
                return;
            }

            ConfigureSqlite(builder, connectionString: null);
        }

        private static void ConfigureSqlite(DbContextOptionsBuilder builder, string? connectionString)
        {
            var sqliteConnection = string.IsNullOrWhiteSpace(connectionString)
                ? "Data Source=ninjabot.db"
                : connectionString;

            builder.UseSqlite(sqliteConnection);
        }

        private static void ConfigurePostgres(DbContextOptionsBuilder builder, string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("Postgres provider selected but ConnectionStrings:NinjaBot is missing.");
            }

            builder.UseNpgsql(connectionString);
        }
    }
}
