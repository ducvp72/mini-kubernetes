using Grpc.Core;
using Microsoft.Extensions.Options;
using MiniK8s.Contracts.Webhook.V1;
using OrderService.Application.Abstractions;
using OrderService.Application.Dtos;
using OrderService.Application.Options;

namespace OrderService.Infrastructure.Grpc;

// Pattern: Adapter — invokes the Go WebhookService over a pooled plaintext HTTP/2 (h2c) gRPC client with bounded deadlines.
public sealed class GrpcWebhookDispatcher : IWebhookDispatcher
{
    private readonly WebhookService.WebhookServiceClient _client;
    private readonly WebhookClientOptions _options;
    private readonly TimeProvider _timeProvider;

    public GrpcWebhookDispatcher(
        WebhookService.WebhookServiceClient client,
        IOptions<WebhookClientOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _client = client;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async Task<WebhookDispatchResult> SendOrderCallbackAsync(WebhookCallbackRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deadline = _timeProvider.GetUtcNow().UtcDateTime.AddSeconds(_options.TimeoutSeconds);
        var grpcRequest = new SendOrderCallbackRequest
        {
            OrderCode = request.OrderCode,
            Status = request.Status,
            CallbackUrl = request.CallbackUrl,
            CorrelationId = request.CorrelationId,
            EventTimestampUnixMs = request.EventTimestampUnixMs
        };

        try
        {
            var response = await _client.SendOrderCallbackAsync(
                grpcRequest,
                deadline: deadline,
                cancellationToken: ct);

            return new WebhookDispatchResult(
                response.Delivered,
                response.HttpStatusCode,
                response.Attempts,
                response.ErrorMessage ?? string.Empty);
        }
        catch (RpcException ex) when (!ct.IsCancellationRequested)
        {
            return new WebhookDispatchResult(
                Delivered: false,
                HttpStatusCode: 0,
                Attempts: 0,
                ErrorMessage: $"gRPC {ex.StatusCode}: {ex.Status.Detail}");
        }
    }
}
