using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NinjaBotCore.Database;
using NinjaBotCore.Services;
using NinjaBotCore.Repositories;
using Xunit;

namespace NinjaBotCore.Tests
{
    /// <summary>
    /// Tests for WordFilterService - word blacklist caching and database operations
    /// Note: The message handling events require Discord.Net mocking which is complex,
    /// so these tests focus on the testable cache and database operations.
    /// </summary>
    public class WordFilterServiceTests : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly NinjaBotEntities _context;
        private readonly WordFilterService _service;

        public WordFilterServiceTests()
        {
            var services = new ServiceCollection();

            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"WordFilterTestDb_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            services.AddSingleton<Discord.WebSocket.DiscordShardedClient>();

            _provider = services.BuildServiceProvider();
            _context = _provider.GetRequiredService<NinjaBotEntities>();
            _service = new WordFilterService(_provider);
        }

        #region Cache Invalidation Tests

        [Fact]
        public void InvalidateWordListCache_CanBeCalledWithoutError()
        {
            // Arrange
            const long serverId = 12345;

            // Act - Should not throw
            var exception = Record.Exception(() => _service.InvalidateWordListCache(serverId));

            // Assert
            Assert.Null(exception);
        }

        [Fact]
        public void InvalidateWordListCache_CanBeCalledMultipleTimes()
        {
            // Arrange
            const long serverId = 23456;

            // Act - Call multiple times
            _service.InvalidateWordListCache(serverId);
            _service.InvalidateWordListCache(serverId);
            _service.InvalidateWordListCache(serverId);

            // Assert - No exception thrown
            Assert.True(true);
        }

        [Fact]
        public void InvalidateWordListCache_HandlesDifferentServers()
        {
            // Arrange
            var serverIds = new long[] { 111, 222, 333, 444, 555 };

            // Act - Invalidate cache for multiple servers
            foreach (var serverId in serverIds)
            {
                var exception = Record.Exception(() => _service.InvalidateWordListCache(serverId));
                Assert.Null(exception);
            }
        }

        #endregion

        #region Database Operations Tests

        [Fact]
        public async Task WordList_CanAddBannedWords()
        {
            // Arrange
            const long serverId = 34567;
            var wordToAdd = new WordList
            {
                ServerId = serverId,
                Word = "bannedword",
                SetById = 111,
                ServerName = "TestServer"
            };

            // Act
            _context.WordList.Add(wordToAdd);
            await _context.SaveChangesAsync();

            // Assert
            var result = await _context.WordList.FirstOrDefaultAsync(w => w.ServerId == serverId);
            Assert.NotNull(result);
            Assert.Equal("bannedword", result.Word);
        }

        [Fact]
        public async Task WordList_CanRetrieveMultipleWords()
        {
            // Arrange
            const long serverId = 45678;
            var words = new[]
            {
                new WordList { ServerId = serverId, Word = "word1" },
                new WordList { ServerId = serverId, Word = "word2" },
                new WordList { ServerId = serverId, Word = "word3" }
            };

            _context.WordList.AddRange(words);
            await _context.SaveChangesAsync();

            // Act
            var result = await _context.WordList
                .Where(w => w.ServerId == serverId)
                .ToListAsync();

            // Assert
            Assert.Equal(3, result.Count);
        }

        [Fact]
        public async Task WordList_SupportsMultipleServers()
        {
            // Arrange
            const long server1 = 56789;
            const long server2 = 67890;

            _context.WordList.AddRange(
                new WordList { ServerId = server1, Word = "server1word" },
                new WordList { ServerId = server2, Word = "server2word" }
            );
            await _context.SaveChangesAsync();

            // Act
            var server1Words = await _context.WordList.Where(w => w.ServerId == server1).ToListAsync();
            var server2Words = await _context.WordList.Where(w => w.ServerId == server2).ToListAsync();

            // Assert
            Assert.Single(server1Words);
            Assert.Equal("server1word", server1Words[0].Word);

            Assert.Single(server2Words);
            Assert.Equal("server2word", server2Words[0].Word);
        }

        [Fact]
        public async Task WordList_CanDeleteWords()
        {
            // Arrange
            const long serverId = 78901;
            var word = new WordList { ServerId = serverId, Word = "deleteme" };
            _context.WordList.Add(word);
            await _context.SaveChangesAsync();

            // Verify it exists
            Assert.NotNull(await _context.WordList.FirstOrDefaultAsync(w => w.ServerId == serverId));

            // Act
            _context.WordList.Remove(word);
            await _context.SaveChangesAsync();

            // Assert
            Assert.Null(await _context.WordList.FirstOrDefaultAsync(w => w.ServerId == serverId));
        }

        [Fact]
        public async Task WordList_CaseInsensitiveMatching_WorksInLinq()
        {
            // Arrange
            const long serverId = 89012;
            _context.WordList.Add(new WordList { ServerId = serverId, Word = "MixedCase" });
            await _context.SaveChangesAsync();

            // Act - Get words and convert to lowercase (as WordFilterService does)
            var bannedWords = await _context.WordList
                .Where(w => w.ServerId == serverId)
                .Select(w => w.Word.ToLower())
                .ToListAsync();

            // Assert
            Assert.Single(bannedWords);
            Assert.Equal("mixedcase", bannedWords[0]);

            // Simulate message checking (as in HandleWordFilter)
            var testMessage = "This message contains MIXEDCASE word";
            var messageContent = testMessage.ToLower();
            Assert.True(bannedWords.Any(word => messageContent.Contains(word)));
        }

        [Fact]
        public async Task WordList_PartialMatching_WorksCorrectly()
        {
            // Arrange - This tests the Contains logic used in HandleWordFilter
            const long serverId = 90123;
            _context.WordList.Add(new WordList { ServerId = serverId, Word = "bad" });
            await _context.SaveChangesAsync();

            var bannedWords = await _context.WordList
                .Where(w => w.ServerId == serverId)
                .Select(w => w.Word.ToLower())
                .ToListAsync();

            // Act & Assert - Partial matching
            Assert.True(bannedWords.Any(word => "this is bad content".Contains(word)));
            Assert.True(bannedWords.Any(word => "badword".Contains(word)));
            Assert.True(bannedWords.Any(word => "verybad".Contains(word)));
            Assert.False(bannedWords.Any(word => "this is good content".Contains(word)));
        }

        #endregion

        #region Repository Tests

        [Fact]
        public async Task WordList_Repository_CanUpsert()
        {
            // Arrange
            const long serverId = 11111;
            const string word = "testword";

            await using var repo = new Repository<WordList>(_context);

            // Act - First upsert creates
            await repo.UpsertAsync(
                w => w.ServerId == serverId && w.Word == word,
                w => { w.ServerName = "UpdatedServer"; },
                () => new WordList
                {
                    ServerId = serverId,
                    Word = word,
                    SetById = 999,
                    ServerName = "OriginalServer"
                });
            await repo.SaveChangesAsync();

            // Assert
            var result = await repo.FirstOrDefaultAsync(w => w.ServerId == serverId && w.Word == word);
            Assert.NotNull(result);
            Assert.Equal("OriginalServer", result.ServerName); // First create

            // Act - Second upsert updates
            await repo.UpsertAsync(
                w => w.ServerId == serverId && w.Word == word,
                w => { w.ServerName = "UpdatedServer"; },
                () => new WordList { ServerId = serverId, Word = word });
            await repo.SaveChangesAsync();

            result = await repo.FirstOrDefaultAsync(w => w.ServerId == serverId && w.Word == word);
            Assert.Equal("UpdatedServer", result.ServerName); // Updated
        }

        #endregion

        #region Disposal Tests

        [Fact]
        public void Dispose_CanBeCalledWithoutError()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"WordFilterDisposeTest_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            services.AddSingleton<Discord.WebSocket.DiscordShardedClient>();
            var provider = services.BuildServiceProvider();

            var service = new WordFilterService(provider);

            // Act - Should not throw
            var exception = Record.Exception(() => service.Dispose());

            // Assert
            Assert.Null(exception);

            // Cleanup
            provider.Dispose();
        }

        [Fact]
        public void Dispose_CanBeCalledMultipleTimes()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.AddDbContext<NinjaBotEntities>(options =>
                options.UseInMemoryDatabase($"WordFilterDisposeTest2_{Guid.NewGuid()}")
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
            services.AddSingleton<Discord.WebSocket.DiscordShardedClient>();
            var provider = services.BuildServiceProvider();

            var service = new WordFilterService(provider);

            // Act - Call dispose multiple times
            service.Dispose();
            var exception = Record.Exception(() => service.Dispose());

            // Assert
            Assert.Null(exception);

            // Cleanup
            provider.Dispose();
        }

        #endregion

        public async ValueTask DisposeAsync()
        {
            _service?.Dispose();
            _context?.Database.EnsureDeleted();
            if (_context != null)
                await _context.DisposeAsync();
            if (_provider != null)
                await _provider.DisposeAsync();
        }
    }
}
