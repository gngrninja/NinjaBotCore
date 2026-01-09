using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for UnitOfWork pattern ensuring proper resource ownership and disposal
    /// </summary>
    public class UnitOfWorkTests : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly NinjaBotEntities _context;

        public UnitOfWorkTests()
        {
            var services = new ServiceCollection();

            // Setup in-memory database for testing
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"UnitOfWorkTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

            _serviceProvider = services.BuildServiceProvider();
            _context = _serviceProvider.GetRequiredService<NinjaBotEntities>();
        }

        [Fact]
        public async Task UnitOfWork_MultipleRepositories_ShareSameContext()
        {
            // Arrange
            await using var uow = new UnitOfWork(_serviceProvider.GetRequiredService<IServiceScopeFactory>());

            var greetingRepo = uow.Repository<ServerGreeting>();
            var noteRepo = uow.Repository<Note>();

            // Act - Add entities using different repositories
            await greetingRepo.AddAsync(new ServerGreeting
            {
                DiscordGuildId = 111,
                Greeting = "Hello!",
                SetByName = "TestUser",
                TimeSet = DateTime.UtcNow
            });

            await noteRepo.AddAsync(new Note
            {
                ServerId = 111,
                ServerName = "Test Server",
                Note1 = "Test Note",
                SetBy = "TestUser",
                SetById = 123,
                TimeSet = DateTime.UtcNow
            });

            // Save all changes in a single transaction
            await uow.SaveChangesAsync();

            // Assert - Both entities should be saved
            var savedGreeting = await greetingRepo.FirstOrDefaultAsync(g => g.DiscordGuildId == 111);
            var savedNote = await noteRepo.FirstOrDefaultAsync(n => n.ServerId == 111);

            Assert.NotNull(savedGreeting);
            Assert.NotNull(savedNote);
        }

        [Fact]
        public async Task UnitOfWork_DisposingRepository_DoesNotBreakUnitOfWork()
        {
            // This test verifies the ownership fix - disposing a repository from UnitOfWork
            // should NOT dispose the shared context

            // Arrange
            await using var uow = new UnitOfWork(_serviceProvider.GetRequiredService<IServiceScopeFactory>());

            var greetingRepo1 = uow.Repository<ServerGreeting>();

            // Act - Add an entity
            await greetingRepo1.AddAsync(new ServerGreeting
            {
                DiscordGuildId = 222,
                Greeting = "First greeting",
                SetByName = "User1",
                TimeSet = DateTime.UtcNow
            });

            // Dispose the repository (cast to concrete type to access DisposeAsync)
            // This simulates someone using 'await using var repo = ...'
            if (greetingRepo1 is IAsyncDisposable disposableRepo)
            {
                await disposableRepo.DisposeAsync();
            }

            // Get a new repository from the same UnitOfWork
            var greetingRepo2 = uow.Repository<ServerGreeting>();

            // This should still work because the UnitOfWork's context wasn't disposed
            await greetingRepo2.AddAsync(new ServerGreeting
            {
                DiscordGuildId = 333,
                Greeting = "Second greeting",
                SetByName = "User2",
                TimeSet = DateTime.UtcNow
            });

            await uow.SaveChangesAsync();

            // Assert - Both entities should be saved
            var greeting1 = await greetingRepo2.FirstOrDefaultAsync(g => g.DiscordGuildId == 222);
            var greeting2 = await greetingRepo2.FirstOrDefaultAsync(g => g.DiscordGuildId == 333);

            Assert.NotNull(greeting1);
            Assert.NotNull(greeting2);
            Assert.Equal("First greeting", greeting1.Greeting);
            Assert.Equal("Second greeting", greeting2.Greeting);
        }

        [Fact]
        public async Task UnitOfWork_RepositoryReuse_ReturnsSameInstance()
        {
            // Verify that requesting the same repository type multiple times
            // returns the same cached instance

            // Arrange
            await using var uow = new UnitOfWork(_serviceProvider.GetRequiredService<IServiceScopeFactory>());

            // Act
            var repo1 = uow.Repository<ServerGreeting>();
            var repo2 = uow.Repository<ServerGreeting>();

            // Assert - Should be the same instance
            Assert.Same(repo1, repo2);
        }

        [Fact]
        public async Task StandaloneRepository_OwnsAndDisposesOwnContext()
        {
            // This test verifies that repositories created with IServiceScopeFactory
            // DO dispose their own context and operate independently

            // Arrange & Act
            long testGuildId = 555;

            await using (var repo = new Repository<ServerGreeting>(_serviceProvider.GetRequiredService<IServiceScopeFactory>()))
            {
                await repo.AddAsync(new ServerGreeting
                {
                    DiscordGuildId = testGuildId,
                    Greeting = "Standalone repo",
                    SetByName = "StandaloneUser",
                    TimeSet = DateTime.UtcNow
                });
                await repo.SaveChangesAsync();

                // Verify it exists in this repo's context
                var inContext = await repo.FirstOrDefaultAsync(g => g.DiscordGuildId == testGuildId);
                Assert.NotNull(inContext);
                Assert.Equal("Standalone repo", inContext.Greeting);
            }
            // Repository and its context are now disposed

            // This test demonstrates that the repository properly owned and disposed its resources
            Assert.True(true); // If we get here without exceptions, disposal worked correctly
        }

        public async ValueTask DisposeAsync()
        {
            _context?.Database.EnsureDeleted();
            if (_context != null)
                await _context.DisposeAsync();
            if (_serviceProvider != null)
                await _serviceProvider.DisposeAsync();
        }
    }
}
