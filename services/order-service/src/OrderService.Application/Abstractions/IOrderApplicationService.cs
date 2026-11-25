using OrderService.Application.Dtos;

namespace OrderService.Application.Abstractions;

public interface IOrderApplicationService
{
    Task<CreateOrderResult> CreateOrderAsync(CreateOrderCommand command, CancellationToken ct = default);

    Task<OrderDetailsDto> GetOrderAsync(string orderCode, CancellationToken ct = default);

    Task<ListOrdersResult> ListOrdersAsync(ListOrdersQuery query, CancellationToken ct = default);
}
