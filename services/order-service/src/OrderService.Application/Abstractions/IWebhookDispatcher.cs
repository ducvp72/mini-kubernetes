using OrderService.Application.Dtos;

namespace OrderService.Application.Abstractions;

public interface IWebhookDispatcher
{
    Task<WebhookDispatchResult> SendOrderCallbackAsync(WebhookCallbackRequest request, CancellationToken ct = default);
}
