using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Storage;
using NinjaBotCore.Database;

namespace NinjaBotCore.Repositories
{
    /// <summary>
    /// Unit of Work pattern for multi-entity operations and transaction support
    /// </summary>
    public interface IUnitOfWork : IDisposable
    {
        /// <summary>
        /// Gets a repository for the specified entity type.
        /// All repositories from the same UnitOfWork share the same database context.
        /// </summary>
        IRepository<TEntity> Repository<TEntity>() where TEntity : class;

        /// <summary>
        /// Begins a database transaction
        /// </summary>
        Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default);

        /// <summary>
        /// Saves all pending changes across all repositories in this unit of work
        /// </summary>
        Task<int> SaveChangesAsync(CancellationToken ct = default);

        /// <summary>
        /// Provides direct access to the underlying DbContext for complex queries
        /// that don't fit the repository pattern (ExecuteDeleteAsync, MaxAsync, etc.)
        /// </summary>
        NinjaBotEntities Context { get; }
    }
}
