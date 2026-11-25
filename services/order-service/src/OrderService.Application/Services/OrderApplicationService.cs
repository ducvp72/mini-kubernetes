using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderService.Application.Abstractions;
using OrderService.Application.Dtos;
using OrderService.Application.Events;
using OrderService.Application.Logging;
using OrderService.Domain.Entities;
using OrderService.Domain.Events;
using OrderService.Domain.Exceptions;
using OrderService.Domain.Pagination;
using OrderService.Domain.Repositories;

namespace OrderService.Application.Services;

public sealed class OrderApplicationService : IOrderApplicationService
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    private const int MaxTransientDeadlockAttempts = 3;

    private static readonly JsonSerializerOptions EventJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IOrderRepository _orderRepository;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICursorTokenCodec _cursorCodec;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrderApplicationService> _logger;

    public OrderApplicationService(
        IOrderRepository orderRepository,
        IOutboxRepository outboxRepository,
        IUnitOfWork unitOfWork,
        ICursorTokenCodec cursorCodec,
        TimeProvider timeProvider,
        ILogger<OrderApplicationService> logger)
    {
        ArgumentNullException.ThrowIfNull(orderRepository);
        ArgumentNullException.ThrowIfNull(outboxRepository);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(cursorCodec);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _orderRepository = orderRepository;
        _outboxRepository = outboxRepository;
        _unitOfWork = unitOfWork;
        _cursorCodec = cursorCodec;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    // Pattern: Unit of Work + Transactional Outbox — persists Order and OutboxMessage atomically with idempotency dedup.
    public async Task<CreateOrderResult> CreateOrderAsync(CreateOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Validate domain invariants before opening any database connection or transaction.
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var candidate = Order.Create(
            command.IdempotencyKey,
            command.CustomerId,
            command.Sku,
            command.Quantity,
            command.UnitPrice,
            command.CallbackUrl,
            command.SimulateWorkMs,
            nowUtc);

        var existingFastPath = await _orderRepository.GetByIdempotencyKeyAsync(candidate.IdempotencyKey, ct);
        if (existingFastPath is not null)
        {
            _logger.LogOrderDeduplicated(existingFastPath.OrderCode);
            return new CreateOrderResult(existingFastPath.OrderCode, existingFastPath.Status, true, existingFastPath.CreatedAt);
        }

        for (var attempt = 1; attempt <= MaxTransientDeadlockAttempts; attempt++)
        {
            var order = attempt == 1
                ? candidate
                : Order.Create(
                    command.IdempotencyKey,
                    command.CustomerId,
                    command.Sku,
                    command.Quantity,
                    command.UnitPrice,
                    command.CallbackUrl,
                    command.SimulateWorkMs,
                    _timeProvider.GetUtcNow().UtcDateTime);

            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            try
            {
                await _orderRepository.AddAsync(order, ct);
                await _unitOfWork.SaveChangesAsync(ct);

                var createdEvent = order.DomainEvents.OfType<OrderCreatedDomainEvent>().First();
                var integrationEvent = new OrderCreatedIntegrationEvent(
                    createdEvent.EventId,
                    order.Id,
                    order.OrderCode,
                    order.CustomerId,
                    order.Sku,
                    order.Quantity,
                    order.UnitPrice,
                    order.CallbackUrl,
                    order.SimulateWorkMs,
                    order.RowVersion,
                    order.CreatedAt);

                var payloadJson = JsonSerializer.Serialize(integrationEvent, EventJsonOptions);
                var outboxMessage = OutboxMessage.Create(
                    order.Id,
                    OrderEventConstants.OrderCreatedEventType,
                    order.OrderCode,
                    payloadJson,
                    order.CreatedAt);

                await _outboxRepository.AddAsync(outboxMessage, ct);
                await _unitOfWork.SaveChangesAsync(ct);

                await transaction.CommitAsync(ct);
                order.ClearDomainEvents();

                _logger.LogOrderCreated(order.OrderCode, order.CustomerId, order.Sku, order.Quantity);
                return new CreateOrderResult(order.OrderCode, order.Status, false, order.CreatedAt);
            }
            catch (Exception ex) when (_unitOfWork.IsDuplicateKeyException(ex))
            {
                await transaction.RollbackAsync(ct);
                _unitOfWork.ClearChangeTracker();

                var existing = await _orderRepository.GetByIdempotencyKeyAsync(order.IdempotencyKey, ct);
                if (existing is not null)
                {
                    _logger.LogOrderDeduplicated(existing.OrderCode);
                    return new CreateOrderResult(existing.OrderCode, existing.Status, true, existing.CreatedAt);
                }

                throw new ConcurrencyConflictException("Concurrent order creation conflicted on unique key.");
            }
            catch (Exception ex) when (_unitOfWork.IsTransientDeadlockException(ex) && attempt < MaxTransientDeadlockAttempts)
            {
                await transaction.RollbackAsync(ct);
                _unitOfWork.ClearChangeTracker();

                var existing = await _orderRepository.GetByIdempotencyKeyAsync(order.IdempotencyKey, ct);
                if (existing is not null)
                {
                    _logger.LogOrderDeduplicated(existing.OrderCode);
                    return new CreateOrderResult(existing.OrderCode, existing.Status, true, existing.CreatedAt);
                }

                _logger.LogOrderCreateDeadlockRetry(attempt, ex);
                await Task.Delay(TimeSpan.FromMilliseconds(15 * attempt), ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                _unitOfWork.ClearChangeTracker();
                throw;
            }
        }

        throw new ConcurrencyConflictException("Order creation failed after bounded deadlock retries.");
    }

    public async Task<OrderDetailsDto> GetOrderAsync(string orderCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orderCode))
        {
            throw new DomainValidationException("orderCode is required.");
        }

        var normalizedCode = orderCode.Trim();
        if (normalizedCode.Length != Order.OrderCodeLength)
        {
            throw new DomainValidationException($"orderCode must be exactly {Order.OrderCodeLength} characters.");
        }

        var order = await _orderRepository.GetByOrderCodeAsync(normalizedCode, ct);
        if (order is null)
        {
            throw new OrderNotFoundException(normalizedCode);
        }

        return new OrderDetailsDto(
            order.OrderCode,
            order.CustomerId,
            order.Sku,
            order.Quantity,
            order.UnitPrice,
            order.TotalAmount,
            order.Status,
            order.CallbackUrl,
            order.CreatedAt,
            order.UpdatedAt);
    }

    // Pattern: Keyset Pagination — bidirectional (created_at, id) traversal with pageSize + 1 lookahead and zero COUNT(*).
    public async Task<ListOrdersResult> ListOrdersAsync(ListOrdersQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var hasNextCursor = !string.IsNullOrWhiteSpace(query.NextCursor);
        var hasPrevCursor = !string.IsNullOrWhiteSpace(query.PrevCursor);

        if (hasNextCursor && hasPrevCursor)
        {
            throw new DomainValidationException("nextCursor and prevCursor are mutually exclusive.");
        }

        var pageSize = query.PageSize == 0 ? DefaultPageSize : query.PageSize;
        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new DomainValidationException($"pageSize must be between 1 and {MaxPageSize}.");
        }

        string? normalizedCustomerId = null;
        if (!string.IsNullOrWhiteSpace(query.CustomerId))
        {
            normalizedCustomerId = query.CustomerId.Trim();
            if (normalizedCustomerId.Length > Order.MaxCustomerIdLength)
            {
                throw new DomainValidationException($"customerId cannot exceed {Order.MaxCustomerIdLength} characters.");
            }
        }

        var direction = hasPrevCursor ? CursorDirection.Backward : CursorDirection.Forward;
        CursorPosition? cursorPosition = null;
        if (hasNextCursor)
        {
            cursorPosition = _cursorCodec.Decode(query.NextCursor!);
        }
        else if (hasPrevCursor)
        {
            cursorPosition = _cursorCodec.Decode(query.PrevCursor!);
        }

        var fetched = await _orderRepository.ListSummariesByCursorAsync(
            normalizedCustomerId,
            pageSize + 1,
            cursorPosition,
            direction,
            ct);

        var hasExtraRow = fetched.Count > pageSize;
        var pageSlice = new List<OrderSummaryView>(Math.Min(fetched.Count, pageSize));
        var limit = Math.Min(fetched.Count, pageSize);
        for (var i = 0; i < limit; i++)
        {
            pageSlice.Add(fetched[i]);
        }

        // Backward queries sort ASC in SQL to fetch immediate predecessors; reverse in memory to preserve DESC display order.
        if (direction == CursorDirection.Backward)
        {
            pageSlice.Reverse();
        }

        if (pageSlice.Count == 0)
        {
            return new ListOrdersResult([], null, null, false, false);
        }

        bool hasNext;
        bool hasPrev;
        if (direction == CursorDirection.Backward)
        {
            hasPrev = hasExtraRow;
            hasNext = true;
        }
        else
        {
            hasNext = hasExtraRow;
            hasPrev = hasNextCursor;
        }

        var first = pageSlice[0];
        var last = pageSlice[^1];
        var nextCursorToken = hasNext ? _cursorCodec.Encode(new CursorPosition(last.CreatedAt, last.Id)) : null;
        var prevCursorToken = hasPrev ? _cursorCodec.Encode(new CursorPosition(first.CreatedAt, first.Id)) : null;

        var items = pageSlice
            .Select(static row => new OrderSummaryDto(
                row.OrderCode,
                row.CustomerId,
                row.Sku,
                row.Quantity,
                row.UnitPrice,
                row.TotalAmount,
                row.Status,
                row.CreatedAt))
            .ToArray();

        return new ListOrdersResult(items, nextCursorToken, prevCursorToken, hasNext, hasPrev);
    }
}
