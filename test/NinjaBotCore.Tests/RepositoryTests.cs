using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;
using Xunit;

namespace NinjaBotCore.Tests
{
    public class RepositoryTests : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public RepositoryTests()
        {
            var services = new ServiceCollection();

            // Setup in-memory database for testing
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}"));

            // Note: IServiceScopeFactory is automatically provided by ServiceProvider - no registration needed

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        [Fact]
        public async Task FirstOrDefaultAsync_ReturnsEntity_WhenExists()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);
            var greeting = new ServerGreeting
            {
                DiscordGuildId = 12345,
                Greeting = "Welcome!",
                GreetUsers = true,
                SetByName = "TestUser",
                TimeSet = DateTime.UtcNow
            };
            await repo.AddAsync(greeting);
            await repo.SaveChangesAsync();

            // Act
            var result = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == 12345);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("Welcome!", result.Greeting);
            Assert.Equal(12345, result.DiscordGuildId);
        }

        [Fact]
        public async Task FirstOrDefaultAsync_ReturnsNull_WhenNotExists()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);

            // Act
            var result = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == 99999);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task WhereAsync_ReturnsMultipleEntities()
        {
            // Arrange
            var repo = new Repository<WowCharAssociation>(_context);
            await repo.AddRangeAsync(new[]
            {
                new WowCharAssociation { UserId = 123, CharName = "Char1", WowRealm = "TestRealm" },
                new WowCharAssociation { UserId = 123, CharName = "Char2", WowRealm = "TestRealm" },
                new WowCharAssociation { UserId = 456, CharName = "Char3", WowRealm = "TestRealm" }
            });
            await repo.SaveChangesAsync();

            // Act
            var result = await repo.WhereAsync(c => c.UserId == 123);

            // Assert
            Assert.Equal(2, result.Count);
            Assert.All(result, c => Assert.Equal(123, c.UserId));
        }

        [Fact]
        public async Task AddAsync_AddsEntityToDatabase()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);
            var greeting = new ServerGreeting
            {
                DiscordGuildId = 54321,
                Greeting = "Hello!",
                GreetUsers = true,
                SetByName = "TestUser2",
                TimeSet = DateTime.UtcNow
            };

            // Act
            await repo.AddAsync(greeting);
            await repo.SaveChangesAsync();

            // Assert
            var saved = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == 54321);
            Assert.NotNull(saved);
            Assert.Equal("Hello!", saved.Greeting);
        }

        [Fact]
        public async Task GetAllAsync_ReturnsAllEntities()
        {
            // Arrange
            var repo = new Repository<ServerGreeting>(_context);
            await repo.AddRangeAsync(new[]
            {
                new ServerGreeting { DiscordGuildId = 1, Greeting = "G1", SetByName = "U1", TimeSet = DateTime.UtcNow },
                new ServerGreeting { DiscordGuildId = 2, Greeting = "G2", SetByName = "U2", TimeSet = DateTime.UtcNow },
                new ServerGreeting { DiscordGuildId = 3, Greeting = "G3", SetByName = "U3", TimeSet = DateTime.UtcNow }
            });
            await repo.SaveChangesAsync();

            // Act
            var result = await repo.GetAllAsync();

            // Assert
            Assert.Equal(3, result.Count);
        }

        public void Dispose()
        {
            _context?.Database.EnsureDeleted();
            _context?.Dispose();
            _serviceProvider?.Dispose();
        }
    }
}
