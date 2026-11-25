namespace OrderService.Domain.Enums;

public enum OutboxMessageStatus : byte
{
    Pending = 0,
    Published = 1,
    DeadLetter = 2
}
