using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;

namespace NinjaBotCore.Repositories
{
    /// <summary>
    /// Generic repository implementation using IServiceScopeFactory for scoped contexts.
    /// This pattern is compatible with singleton services and prevents DI lifetime violations.
    /// </summary>
    /// <typeparam name="TEntity">The entity type</typeparam>
    public class Repository<TEntity> : IRepository<TEntity>, IAsyncDisposable where TEntity : class
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly object _lock = new object();
        private IServiceScope _scope;
        private NinjaBotEntities _context;
        private DbSet<TEntity> _dbSet;
        private bool _disposed;
        private readonly bool _ownsContext;

        /// <summary>
        /// Constructor for standalone repository usage (Pattern #1).
        /// Repository will create and manage its own scope and DbContext.
        /// Use this when creating repositories directly in singleton services.
        /// Internal to prevent DI ambiguity - only called explicitly by GetRepository() helpers.
        /// </summary>
        internal Repository(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _ownsContext = true; // This repository will create and own the context
        }

        /// <summary>
        /// Constructor for DI-resolved or UnitOfWork repository usage.
        /// Repository receives an externally-managed DbContext and does NOT dispose it.
        /// Use this when resolving repositories from a service scope or via UnitOfWork.
        /// This is the preferred constructor for Pattern #3 (marked with ActivatorUtilitiesConstructor).
        /// </summary>
        [ActivatorUtilitiesConstructor]
        public Repository(NinjaBotEntities context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _dbSet = _context.Set<TEntity>();
            _ownsContext = false; // Context is owned by the scope or UnitOfWork
        }

        /// <summary>
        /// Ensures a database context is available (lazy initialization with thread safety)
        /// </summary>
        private void EnsureContext()
        {
            if (_context == null)
            {
                lock (_lock)
                {
                    if (_context == null)
                    {
                        _scope = _scopeFactory.CreateScope();
                        _context = _scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                        _dbSet = _context.Set<TEntity>();
                    }
                }
            }
        }

        public async Task<TEntity> FirstOrDefaultAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default)
        {
            EnsureContext();
            return await _dbSet.FirstOrDefaultAsync(predicate, ct);
        }

        public async Task<List<TEntity>> GetAllAsync(CancellationToken ct = default)
        {
            EnsureContext();
            return await _dbSet.ToListAsync(ct);
        }

        public async Task<List<TEntity>> WhereAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default)
        {
            EnsureContext();
            return await _dbSet.Where(predicate).ToListAsync(ct);
        }

        /// <summary>
        /// Exposes the queryable DbSet for advanced LINQ queries.
        /// Use this when you need database-side operations like Contains with large lists.
        /// </summary>
        public IQueryable<TEntity> Query
        {
            get
            {
                EnsureContext();
                return _dbSet;
            }
        }

        public async Task<TEntity> UpsertAsync(
            Expression<Func<TEntity, bool>> findPredicate,
            Action<TEntity> updateAction,
            Func<TEntity> createFactory,
            CancellationToken ct = default)
        {
            EnsureContext();

            var existing = await _dbSet.FirstOrDefaultAsync(findPredicate, ct);

            if (existing == null)
            {
                // Create new entity
                existing = createFactory();
                await _dbSet.AddAsync(existing, ct);
            }
            else
            {
                // Update existing entity
                updateAction(existing);
            }

            return existing;
        }

        public async Task AddAsync(TEntity entity, CancellationToken ct = default)
        {
            EnsureContext();
            await _dbSet.AddAsync(entity, ct);
        }

        public async Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken ct = default)
        {
            EnsureContext();
            await _dbSet.AddRangeAsync(entities, ct);
        }

        public void Update(TEntity entity)
        {
            EnsureContext();
            _dbSet.Update(entity);
        }

        public void Delete(TEntity entity)
        {
            EnsureContext();
            _dbSet.Remove(entity);
        }

        public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            EnsureContext();
            return await _context.SaveChangesAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                // Only dispose resources we own
                if (_ownsContext)
                {
                    if (_context != null)
                    {
                        await _context.DisposeAsync();
                    }
                    _scope?.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                // Only dispose resources we own
                if (_ownsContext)
                {
                    _context?.Dispose();
                    _scope?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
