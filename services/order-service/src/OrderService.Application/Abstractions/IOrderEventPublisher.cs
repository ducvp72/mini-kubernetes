namespace OrderService.Application.Abstractions;

public interface IOrderEventPublisher
{
    Task PublishAsync(string eventType, string partitionKey, string payload, CancellationToken ct = default);
}
