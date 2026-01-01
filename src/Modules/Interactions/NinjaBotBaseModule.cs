using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
using NinjaBotCore.Repositories;
using System;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions
{
    /// <summary>
    /// Base class for all NinjaBot interaction modules providing database access helpers
    /// </summary>
    public abstract class NinjaBotBaseModule : InteractionModuleBase<ShardedInteractionContext>
    {
        protected readonly IServiceScopeFactory _scopeFactory;

        protected NinjaBotBaseModule(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        /// <summary>
        /// Executes an action with database context
        /// </summary>
        protected async Task WithDbAsync(Func<NinjaBotEntities, Task> action)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            await action(db);
        }

        /// <summary>
        /// Executes a function with database context and returns a value
        /// </summary>
        protected async Task<T> WithDbAsync<T>(Func<NinjaBotEntities, Task<T>> func)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            return await func(db);
        }

        /// <summary>
        /// Synchronously executes an action with database context
        /// </summary>
        protected void WithDb(Action<NinjaBotEntities> action)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            action(db);
        }

        /// <summary>
        /// Synchronously executes a function with database context and returns a value
        /// </summary>
        protected T WithDb<T>(Func<NinjaBotEntities, T> func)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            return func(db);
        }

        /// <summary>
        /// Gets a repository for the specified entity type.
        /// The repository manages its own scope internally using the IServiceScopeFactory.
        ///
        /// ⚠️ CRITICAL: The returned repository OWNS its IServiceScope and DbContext.
        /// You MUST wrap it in 'await using' to prevent memory leaks:
        ///
        /// ✅ CORRECT: await using var repo = GetRepository&lt;MyEntity&gt;();
        /// ❌ WRONG:   var repo = GetRepository&lt;MyEntity&gt;();  // Memory leak!
        ///
        /// For simpler usage, prefer WithScopedRepositoryAsync() which handles disposal automatically.
        /// </summary>
        /// <typeparam name="TEntity">The entity type</typeparam>
        /// <returns>A repository instance for the entity (must be disposed with 'await using')</returns>
        protected IRepository<TEntity> GetRepository<TEntity>() where TEntity : class
        {
            // Repository creates its own scope internally via IServiceScopeFactory
            return new Repository<TEntity>(_scopeFactory);
        }

        /// <summary>
        /// Gets a Unit of Work for multi-entity operations or transactions.
        /// All repositories from the same UnitOfWork share the same database context.
        ///
        /// ⚠️ CRITICAL: The returned UnitOfWork OWNS its IServiceScope and DbContext.
        /// You MUST wrap it in 'await using' to prevent memory leaks:
        ///
        /// ✅ CORRECT: await using var uow = GetUnitOfWork();
        /// ❌ WRONG:   var uow = GetUnitOfWork();  // Memory leak!
        /// ❌ WRONG:   using var uow = GetUnitOfWork();  // Uses sync disposal instead of async!
        ///
        /// For simpler usage, prefer WithScopedUnitOfWorkAsync() which handles disposal automatically.
        /// </summary>
        /// <returns>A unit of work instance (must be disposed with 'await using')</returns>
        protected IUnitOfWork GetUnitOfWork()
        {
            // UnitOfWork creates and manages its own scope
            return new UnitOfWork(_scopeFactory);
        }

        /// <summary>
        /// Executes an async function with a scoped repository.
        /// RECOMMENDED PATTERN for Discord bot commands - creates one scope per command.
        /// This is the cleanest DI-centric approach.
        /// </summary>
        /// <example>
        /// await WithScopedRepositoryAsync&lt;User, bool&gt;(async repo => {
        ///     var user = await repo.FirstOrDefaultAsync(u => u.Id == userId);
        ///     user.LastSeen = DateTime.UtcNow;
        ///     await repo.SaveChangesAsync();
        ///     return true;
        /// });
        /// </example>
        protected async Task<TResult> WithScopedRepositoryAsync<TEntity, TResult>(
            Func<IRepository<TEntity>, Task<TResult>> action) where TEntity : class
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            await using var repo = new Repository<TEntity>(context);
            return await action(repo);
        }

        /// <summary>
        /// Executes an async action with a scoped repository (no return value).
        /// RECOMMENDED PATTERN for Discord bot commands - creates one scope per command.
        /// </summary>
        protected async Task WithScopedRepositoryAsync<TEntity>(
            Func<IRepository<TEntity>, Task> action) where TEntity : class
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            await using var repo = new Repository<TEntity>(context);
            await action(repo);
        }

        /// <summary>
        /// Executes an async function with a scoped Unit of Work.
        /// RECOMMENDED PATTERN for multi-repository operations with transactions.
        /// </summary>
        /// <example>
        /// await WithScopedUnitOfWorkAsync(async uow => {
        ///     var userRepo = uow.Repository&lt;User&gt;();
        ///     var logRepo = uow.Repository&lt;AuditLog&gt;();
        ///
        ///     await userRepo.AddAsync(newUser);
        ///     await logRepo.AddAsync(auditEntry);
        ///     await uow.SaveChangesAsync();
        ///
        ///     return newUser.Id;
        /// });
        /// </example>
        protected async Task<TResult> WithScopedUnitOfWorkAsync<TResult>(
            Func<IUnitOfWork, Task<TResult>> action)
        {
            using var scope = _scopeFactory.CreateScope();
            await using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            return await action(uow);
        }

        /// <summary>
        /// Executes an async action with a scoped Unit of Work (no return value).
        /// RECOMMENDED PATTERN for multi-repository operations with transactions.
        /// </summary>
        protected async Task WithScopedUnitOfWorkAsync(Func<IUnitOfWork, Task> action)
        {
            using var scope = _scopeFactory.CreateScope();
            await using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await action(uow);
        }
    }
}
