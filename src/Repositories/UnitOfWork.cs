using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Database;

namespace NinjaBotCore.Repositories
{
    /// <summary>
    /// Unit of Work implementation providing shared context across multiple repositories
    /// and transaction support for complex multi-entity operations
    /// </summary>
    public class UnitOfWork : IUnitOfWork
    {
        private readonly IServiceScope _scope;
        private readonly NinjaBotEntities _context;
        private readonly Dictionary<Type, object> _repositories;
        private bool _disposed;

        public UnitOfWork(IServiceScopeFactory scopeFactory)
        {
            if (scopeFactory == null)
                throw new ArgumentNullException(nameof(scopeFactory));

            _scope = scopeFactory.CreateScope();
            _context = _scope.ServiceProvider.GetRequiredService<NinjaBotEntities>();
            _repositories = new Dictionary<Type, object>();
        }

        public NinjaBotEntities Context => _context;

        public IRepository<TEntity> Repository<TEntity>() where TEntity : class
        {
            var type = typeof(TEntity);

            if (!_repositories.ContainsKey(type))
            {
                // Create repository with shared context
                _repositories[type] = new Repository<TEntity>(_context);
            }

            return (IRepository<TEntity>)_repositories[type];
        }

        public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken ct = default)
        {
            return await _context.Database.BeginTransactionAsync(ct);
        }

        public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
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
