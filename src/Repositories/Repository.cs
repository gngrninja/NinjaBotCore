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
    public class Repository<TEntity> : IRepository<TEntity> where TEntity : class
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private IServiceScope _scope;
        private NinjaBotEntities _context;
        private DbSet<TEntity> _dbSet;
        private bool _disposed;

        public Repository(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        }

        /// <summary>
        /// Internal constructor for UnitOfWork pattern - receives direct context
        /// </summary>
        internal Repository(NinjaBotEntities context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _dbSet = _context.Set<TEntity>();
        }

        /// <summary>
        /// Ensures a database context is available (lazy initialization)
        /// </summary>
        private void EnsureContext()
        {
            if (_context == null)
            {
                _scope = _scopeFactory.CreateScope();
                _context = _scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
                _dbSet = _context.Set<TEntity>();
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

        public void Dispose()
        {
            if (!_disposed)
            {
                _scope?.Dispose();
                _disposed = true;
            }
        }
    }
}
