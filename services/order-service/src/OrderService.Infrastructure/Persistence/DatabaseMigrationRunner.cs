using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderService.Application.Logging;
using OrderService.Application.Options;

namespace OrderService.Infrastructure.Persistence;

public sealed class DatabaseMigrationRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DatabaseOptions _options;
    private readonly ILogger<DatabaseMigrationRunner> _logger;

    public DatabaseMigrationRunner(
        IServiceScopeFactory scopeFactory,
        IOptions<DatabaseOptions> options,
        ILogger<DatabaseMigrationRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ApplyMigrationsIfEnabledAsync(CancellationToken ct = default)
    {
        if (!_options.ApplyMigrationsOnStartup)
        {
            return;
        }

        for (var attempt = 1; attempt <= _options.MigrationMaxRetries; attempt++)
        {
            try
            {
                _logger.LogApplyingMigrations(attempt, _options.MigrationMaxRetries);

                await using var scope = _scopeFactory.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
                await dbContext.Database.MigrateAsync(ct);

                _logger.LogMigrationsApplied();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < _options.MigrationMaxRetries)
            {
                _logger.LogMigrationRetry(attempt, _options.MigrationMaxRetries, _options.MigrationRetryDelayMs, ex);
                await Task.Delay(TimeSpan.FromMilliseconds(_options.MigrationRetryDelayMs), ct);
            }
        }
    }
}
