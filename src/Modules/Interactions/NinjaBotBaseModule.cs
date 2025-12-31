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
        /// </summary>
        /// <typeparam name="TEntity">The entity type</typeparam>
        /// <returns>A repository instance for the entity</returns>
        protected IRepository<TEntity> GetRepository<TEntity>() where TEntity : class
        {
            // Repository creates its own scope internally via IServiceScopeFactory
            return new Repository<TEntity>(_scopeFactory);
        }

        /// <summary>
        /// Gets a Unit of Work for multi-entity operations or transactions.
        /// All repositories from the same UnitOfWork share the same database context.
        /// Use within a using statement to ensure proper disposal.
        /// </summary>
        /// <returns>A unit of work instance</returns>
        protected IUnitOfWork GetUnitOfWork()
        {
            // UnitOfWork creates and manages its own scope
            return new UnitOfWork(_scopeFactory);
        }
    }
}
