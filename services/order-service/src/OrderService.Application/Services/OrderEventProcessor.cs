using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderService.Application.Abstractions;
using OrderService.Application.Dtos;
using OrderService.Application.Events;
using OrderService.Application.Logging;
using OrderService.Domain.Entities;
using OrderService.Domain.Repositories;

namespace OrderService.Application.Services;

// Pattern: Idempotent Consumer — confirms PENDING orders atomically and dispatches webhooks exactly once per transition.
public sealed class OrderEventProcessor
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IOrderRepository _orderRepository;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrderEventProcessor> _logger;

    public OrderEventProcessor(
        IOrderRepository orderRepository,
        IWebhookDispatcher webhookDispatcher,
        TimeProvider timeProvider,
        ILogger<OrderEventProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(orderRepository);
        ArgumentNullException.ThrowIfNull(webhookDispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _orderRepository = orderRepository;
        _webhookDispatcher = webhookDispatcher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<bool> ProcessMessageAsync(string messageKey, string payloadJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            _logger.LogMalformedOrderEvent(messageKey ?? string.Empty);
            return false;
        }

        OrderCreatedIntegrationEvent? orderEvent;
        try
        {
            orderEvent = JsonSerializer.Deserialize<OrderCreatedIntegrationEvent>(payloadJson, SerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogMalformedOrderEvent(messageKey ?? string.Empty, ex);
            return false;
        }

        if (orderEvent is null || orderEvent.OrderId == 0 || string.IsNullOrWhiteSpace(orderEvent.OrderCode))
        {
            _logger.LogMalformedOrderEvent(messageKey ?? string.Empty);
            return false;
        }

        var boundedWorkMs = Math.Min(orderEvent.SimulateWorkMs, Order.MaxSimulateWorkMs);
        if (boundedWorkMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(boundedWorkMs), ct);
        }

        var expectedVersion = orderEvent.ExpectedRowVersion == 0 ? 1UL : orderEvent.ExpectedRowVersion;
        var confirmed = await _orderRepository.TryConfirmOrderAsync(orderEvent.OrderId, expectedVersion, ct);
        if (!confirmed)
        {
            _logger.LogOrderEventSkippedDuplicate(orderEvent.OrderCode, orderEvent.OrderId);
            return false;
        }

        _logger.LogOrderConfirmed(orderEvent.OrderCode, orderEvent.OrderId);

        if (string.IsNullOrWhiteSpace(orderEvent.CallbackUrl))
        {
            _logger.LogWebhookSkippedEmptyUrl(orderEvent.OrderCode);
            return true;
        }

        var callbackRequest = new WebhookCallbackRequest(
            orderEvent.OrderCode,
            "CONFIRMED",
            orderEvent.CallbackUrl,
            orderEvent.EventId.ToString("D"),
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

        var dispatchResult = await _webhookDispatcher.SendOrderCallbackAsync(callbackRequest, ct);
        if (dispatchResult.Delivered)
        {
            _logger.LogWebhookDelivered(orderEvent.OrderCode, dispatchResult.HttpStatusCode, dispatchResult.Attempts);
        }
        else
        {
            _logger.LogWebhookDeliveryFailed(
                orderEvent.OrderCode,
                dispatchResult.HttpStatusCode,
                dispatchResult.Attempts,
                dispatchResult.ErrorMessage);
        }

        return true;
    }
}
