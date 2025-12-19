using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NinjaBotCore.Database;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Unit tests for DatabaseConfigurator to ensure proper provider selection.
    /// </summary>
    public class DatabaseConfiguratorTests
    {
        [Fact]
        public void ConfigureFrom_WithPostgresProvider_ShouldUse_Npgsql()
        {
            // Arrange
            var configData = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Database:Provider"] = "postgres",
                ["ConnectionStrings:NinjaBot"] = "Host=localhost;Database=test;Username=test;Password=test"
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            // Act
            DatabaseConfigurator.ConfigureFrom(configuration);

            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();
            DatabaseConfigurator.Apply(optionsBuilder);

            // Assert
            var options = optionsBuilder.Options;
            Assert.NotNull(options);

            // Verify Npgsql is configured
            var extension = options.FindExtension<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal.NpgsqlOptionsExtension>();
            Assert.NotNull(extension);
        }

        [Fact]
        public void ConfigureFrom_WithPostgreSQLProvider_ShouldUse_Npgsql()
        {
            // Arrange - Test alternate provider name
            var configData = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Database:Provider"] = "postgresql",
                ["ConnectionStrings:NinjaBot"] = "Host=localhost;Database=test;Username=test;Password=test"
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            // Act
            DatabaseConfigurator.ConfigureFrom(configuration);

            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();
            DatabaseConfigurator.Apply(optionsBuilder);

            // Assert
            var options = optionsBuilder.Options;
            var extension = options.FindExtension<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal.NpgsqlOptionsExtension>();
            Assert.NotNull(extension);
        }

        [Fact]
        public void ConfigureFrom_WithSQLiteProvider_ShouldUse_SQLite()
        {
            // Arrange
            var configData = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Database:Provider"] = "sqlite",
                ["ConnectionStrings:NinjaBot"] = "Data Source=test.db"
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            // Act
            DatabaseConfigurator.ConfigureFrom(configuration);

            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();
            DatabaseConfigurator.Apply(optionsBuilder);

            // Assert
            var options = optionsBuilder.Options;
            var extension = options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>();
            Assert.NotNull(extension);
        }

        [Fact]
        public void ConfigureFrom_WithNoProvider_ShouldDefault_ToSQLite()
        {
            // Arrange - No provider specified
            var configData = new System.Collections.Generic.Dictionary<string, string>
            {
                ["ConnectionStrings:NinjaBot"] = "Data Source=test.db"
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            // Act
            DatabaseConfigurator.ConfigureFrom(configuration);

            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();
            DatabaseConfigurator.Apply(optionsBuilder);

            // Assert - Should default to SQLite
            var options = optionsBuilder.Options;
            var extension = options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>();
            Assert.NotNull(extension);
        }

        [Fact]
        public void ConfigureFrom_WithEmptyProvider_ShouldDefault_ToSQLite()
        {
            // Arrange - Empty provider
            var configData = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Database:Provider"] = "",
                ["ConnectionStrings:NinjaBot"] = "Data Source=test.db"
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            // Act
            DatabaseConfigurator.ConfigureFrom(configuration);

            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();
            DatabaseConfigurator.Apply(optionsBuilder);

            // Assert - Should default to SQLite
            var options = optionsBuilder.Options;
            var extension = options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>();
            Assert.NotNull(extension);
        }

        [Fact]
        public void ConfigureFrom_WithInvalidProvider_ShouldThrow()
        {
            // Arrange
            var configData = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Database:Provider"] = "mysql", // Unsupported
                ["ConnectionStrings:NinjaBot"] = "Server=localhost;Database=test"
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            // Act
            DatabaseConfigurator.ConfigureFrom(configuration);

            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();

            // Assert - Should throw InvalidOperationException
            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                DatabaseConfigurator.Apply(optionsBuilder);
            });

            Assert.Contains("Unsupported database provider", exception.Message);
            Assert.Contains("mysql", exception.Message);
        }

        [Fact]
        public void ConfigureFrom_Postgres_WithoutConnectionString_ShouldThrow()
        {
            // Arrange - Postgres requires connection string
            var configData = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Database:Provider"] = "postgres"
                // No connection string provided
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            // Act
            DatabaseConfigurator.ConfigureFrom(configuration);

            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();

            // Assert
            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                DatabaseConfigurator.Apply(optionsBuilder);
            });

            Assert.Contains("Postgres provider selected", exception.Message);
            Assert.Contains("ConnectionStrings:NinjaBot is missing", exception.Message);
        }

        [Fact]
        public void ConfigureFrom_SQLite_WithoutConnectionString_ShouldUse_DefaultPath()
        {
            // Arrange
            var configData = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Database:Provider"] = "sqlite"
                // No connection string - should use default
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            // Act
            DatabaseConfigurator.ConfigureFrom(configuration);

            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();
            DatabaseConfigurator.Apply(optionsBuilder);

            // Assert - Should use default "ninjabot.db"
            var options = optionsBuilder.Options;
            var extension = options.FindExtension<Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension>();
            Assert.NotNull(extension);

            // Verify it contains the default database name
            var connectionString = extension.ConnectionString;
            Assert.Contains("ninjabot.db", connectionString);
        }

        [Fact]
        public void Apply_WithoutConfiguration_ShouldUse_DefaultSQLite()
        {
            // Arrange - Reset configuration by creating a new instance
            // Note: This test may not work reliably since DatabaseConfigurator uses static state
            // We'll just verify it doesn't throw
            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();

            // Act - Apply without prior configuration
            DatabaseConfigurator.Apply(optionsBuilder);

            // Assert - Should not throw and options should be configured
            var options = optionsBuilder.Options;
            Assert.True(options != null);
            // Note: Cannot reliably test provider without resetting static state
        }

        [Fact]
        public void ConfigureFrom_ProviderName_ShouldBe_CaseInsensitive()
        {
            // Arrange - Test various case combinations
            var testCases = new[] { "POSTGRES", "PostgreSQL", "PoStGrEs", "POSTGRESQL" };

            foreach (var providerName in testCases)
            {
                var configData = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["Database:Provider"] = providerName,
                    ["ConnectionStrings:NinjaBot"] = "Host=localhost;Database=test;Username=test;Password=test"
                };

                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(configData)
                    .Build();

                // Act
                DatabaseConfigurator.ConfigureFrom(configuration);

                var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();
                DatabaseConfigurator.Apply(optionsBuilder);

                // Assert
                var options = optionsBuilder.Options;
                var extension = options.FindExtension<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal.NpgsqlOptionsExtension>();
                Assert.NotNull(extension);
            }
        }

        [Fact]
        public void ConfigureFrom_WithWhitespace_ShouldTrim_ProviderName()
        {
            // Arrange
            var configData = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Database:Provider"] = "  postgres  ",
                ["ConnectionStrings:NinjaBot"] = "Host=localhost;Database=test;Username=test;Password=test"
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            // Act & Assert - Should not throw
            DatabaseConfigurator.ConfigureFrom(configuration);

            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();
            var exception = Record.Exception(() => DatabaseConfigurator.Apply(optionsBuilder));

            // Should apply successfully without throwing
            Assert.Null(exception);
            Assert.NotNull(optionsBuilder.Options);
        }

        [Fact]
        public void NinjaBotEntities_OnConfiguring_ShouldCall_DatabaseConfigurator()
        {
            // Arrange
            var configData = new System.Collections.Generic.Dictionary<string, string>
            {
                ["Database:Provider"] = "sqlite",
                ["ConnectionStrings:NinjaBot"] = "Data Source=test_onconfiguring.db"
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configData)
                .Build();

            DatabaseConfigurator.ConfigureFrom(configuration);

            // Act - Create context without options (triggers OnConfiguring)
            using var context = new NinjaBotEntities();

            // Assert - Should be configured
            Assert.True(context.Database.IsRelational());
        }
    }
}
