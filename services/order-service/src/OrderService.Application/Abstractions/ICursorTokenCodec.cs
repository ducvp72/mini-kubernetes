using OrderService.Domain.Pagination;

namespace OrderService.Application.Abstractions;

public interface ICursorTokenCodec
{
    string Encode(CursorPosition position);

    CursorPosition Decode(string cursorToken);
}
