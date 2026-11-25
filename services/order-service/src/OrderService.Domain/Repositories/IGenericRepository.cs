using OrderService.Domain.Common;

namespace OrderService.Domain.Repositories;

// Pattern: Repository — abstracts aggregate CRUD persistence behind a domain-owned contract.
public interface IGenericRepository<TEntity, TId>
    where TEntity : BaseEntity<TId>
    where TId : notnull
{
    Task<TEntity?> GetByIdAsync(TId id, CancellationToken ct = default);

    Task AddAsync(TEntity entity, CancellationToken ct = default);

    void Update(TEntity entity);

    void Remove(TEntity entity);
}
