namespace OrderService.Domain.Repositories;

// Pattern: Unit of Work — coordinates atomic transactions across Order and Outbox repositories.
public interface IUnitOfWork
{
    Task<ITransactionScope> BeginTransactionAsync(CancellationToken ct = default);

    Task<int> SaveChangesAsync(CancellationToken ct = default);

    void ClearChangeTracker();

    bool IsDuplicateKeyException(Exception exception);

    bool IsTransientDeadlockException(Exception exception);
}

public interface ITransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);

    Task RollbackAsync(CancellationToken ct = default);
}
