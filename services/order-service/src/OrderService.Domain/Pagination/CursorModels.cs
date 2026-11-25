namespace OrderService.Domain.Pagination;

public enum CursorDirection
{
    Forward = 0,
    Backward = 1
}

// Pattern: Keyset Pagination — deterministic (created_at, id) cursor key eliminates O(N) OFFSET scans.
public sealed record CursorPosition(DateTime CreatedAtUtc, ulong Id);
