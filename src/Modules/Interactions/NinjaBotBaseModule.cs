using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;
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
    }
}
