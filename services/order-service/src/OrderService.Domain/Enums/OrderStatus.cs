namespace OrderService.Domain.Enums;

public enum OrderStatus : byte
{
    Unspecified = 0,
    Pending = 1,
    Confirmed = 2,
    Failed = 3
}
