namespace OrderService.Domain.Exceptions;

public sealed class OrderNotFoundException : Exception
{
    public string OrderCode { get; }

    public OrderNotFoundException(string orderCode)
        : base($"Order '{orderCode}' was not found.")
    {
        OrderCode = orderCode;
    }
}
