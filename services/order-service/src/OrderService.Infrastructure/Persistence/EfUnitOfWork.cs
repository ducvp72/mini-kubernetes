using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;
using OrderService.Domain.Repositories;

namespace OrderService.Infrastructure.Persistence;

// Pattern: Unit of Work — binds OrderDbContext transactions and SQL Server concurrency error classification.
public sealed class EfUnitOfWork : IUnitOfWork
{
    private const int SqlServerDuplicateKeyIndexErrorCode = 2601;
    private const int SqlServerDuplicateKeyConstraintErrorCode = 2627;
    private const int SqlServerDeadlockErrorCode = 1205;
    private const int SqlServerLockTimeoutErrorCode = 1222;

    private readonly OrderDbContext _dbContext;

    public EfUnitOfWork(OrderDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<ITransactionScope> BeginTransactionAsync(CancellationToken ct = default)
    {
        var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        return new EfTransactionScope(transaction);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return _dbContext.SaveChangesAsync(ct);
    }

    public void ClearChangeTracker()
    {
        _dbContext.ChangeTracker.Clear();
    }

    public bool IsDuplicateKeyException(Exception exception)
    {
        return HasSqlErrorNumber(exception, SqlServerDuplicateKeyIndexErrorCode) ||
               HasSqlErrorNumber(exception, SqlServerDuplicateKeyConstraintErrorCode);
    }

    public bool IsTransientDeadlockException(Exception exception)
    {
        return HasSqlErrorNumber(exception, SqlServerDeadlockErrorCode) ||
               HasSqlErrorNumber(exception, SqlServerLockTimeoutErrorCode);
    }

    private static bool HasSqlErrorNumber(Exception exception, int targetErrorCode)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlEx && sqlEx.Number == targetErrorCode)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class EfTransactionScope : ITransactionScope
    {
        private readonly IDbContextTransaction _transaction;
        private bool _completed;

        public EfTransactionScope(IDbContextTransaction transaction)
        {
            _transaction = transaction;
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await _transaction.CommitAsync(ct);
            _completed = true;
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            if (_completed)
            {
                return;
            }

            await _transaction.RollbackAsync(ct);
            _completed = true;
        }

        public ValueTask DisposeAsync()
        {
            return _transaction.DisposeAsync();
        }
    }
}
