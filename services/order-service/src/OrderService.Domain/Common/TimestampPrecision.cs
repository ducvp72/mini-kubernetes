namespace OrderService.Domain.Common;

/// <summary>
/// Normalizes UTC timestamps to MySQL DATETIME(6) microsecond precision (1 microsecond = 10 ticks)
/// so in-memory entities and persisted cursor keys match deterministically.
/// </summary>
public static class TimestampPrecision
{
    private const long TicksPerMicrosecond = 10;

    public static DateTime ToUtcMicroseconds(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        var truncatedTicks = utc.Ticks - (utc.Ticks % TicksPerMicrosecond);
        return new DateTime(truncatedTicks, DateTimeKind.Utc);
    }
}
