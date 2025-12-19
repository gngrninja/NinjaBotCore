using System;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace NinjaBotCore.Database
{
public class NinjaBotEntitiesFactory : IDesignTimeDbContextFactory<NinjaBotEntities>
    {
        public NinjaBotEntities CreateDbContext(string[] args)
        {
            var configuration = BuildConfiguration();
            DatabaseConfigurator.ConfigureFrom(configuration);

            var optionsBuilder = new DbContextOptionsBuilder<NinjaBotEntities>();
            DatabaseConfigurator.Apply(optionsBuilder);

            return new NinjaBotEntities(optionsBuilder.Options);
        }

        private static IConfigurationRoot BuildConfiguration()
        {
            var basePath = Directory.GetCurrentDirectory();
            var configCandidates = new[]
            {
                Environment.GetEnvironmentVariable("NINJABOT_CONFIG_PATH"),
                Path.Combine("config", "config.json"),
                "config.json"
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.IsPathRooted(path) ? path : Path.Combine(basePath, path))
            .ToList();

            var resolvedConfigPath = configCandidates.FirstOrDefault(File.Exists);
            if (resolvedConfigPath == null)
            {
                throw new FileNotFoundException($"Unable to locate NinjaBot configuration. Looked in: {string.Join(", ", configCandidates)}");
            }

            var fileProvider = new PhysicalFileProvider(Path.GetDirectoryName(resolvedConfigPath)!);
            var configFileName = Path.GetFileName(resolvedConfigPath);

            return new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile(fileProvider, configFileName, optional: false, reloadOnChange: false)
                .AddEnvironmentVariables(prefix: "NINJABOT_")
                .Build();
        }
    }
}
