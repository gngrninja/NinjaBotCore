using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace NinjaBotCore.Repositories
{
    /// <summary>
    /// Generic repository interface for database operations
    /// </summary>
    /// <typeparam name="TEntity">The entity type</typeparam>
    public interface IRepository<TEntity> : IAsyncDisposable where TEntity : class
    {
        /// <summary>
        /// Gets the first entity matching the predicate or null
        /// </summary>
        Task<TEntity> FirstOrDefaultAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default);

        /// <summary>
        /// Gets all entities of this type
        /// </summary>
        Task<List<TEntity>> GetAllAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets all entities matching the predicate
        /// </summary>
        Task<List<TEntity>> WhereAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default);

        /// <summary>
        /// Inserts or updates an entity based on the find predicate.
        /// This is the critical method for the 39% of usage that follows the upsert pattern.
        /// </summary>
        /// <param name="findPredicate">Expression to find existing entity</param>
        /// <param name="updateAction">Action to update existing entity properties</param>
        /// <param name="createFactory">Factory function to create new entity if not found</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>The upserted entity</returns>
        Task<TEntity> UpsertAsync(
            Expression<Func<TEntity, bool>> findPredicate,
            Action<TEntity> updateAction,
            Func<TEntity> createFactory,
            CancellationToken ct = default);

        /// <summary>
        /// Adds a new entity to the context
        /// </summary>
        Task AddAsync(TEntity entity, CancellationToken ct = default);

        /// <summary>
        /// Adds multiple entities to the context
        /// </summary>
        Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken ct = default);

        /// <summary>
        /// Marks an entity as modified
        /// </summary>
        void Update(TEntity entity);

        /// <summary>
        /// Marks an entity for deletion
        /// </summary>
        void Delete(TEntity entity);

        /// <summary>
        /// Saves all pending changes to the database
        /// </summary>
        /// <returns>Number of entities affected</returns>
        Task<int> SaveChangesAsync(CancellationToken ct = default);
    }
}
