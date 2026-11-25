using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrderService.Application.Abstractions;
using OrderService.Domain.Common;
using OrderService.Domain.Exceptions;
using OrderService.Domain.Pagination;

namespace OrderService.Application.Pagination;

// Pattern: Keyset Pagination — encodes and validates opaque Base64url JSON (created_at, id) cursors.
public sealed class CursorTokenCodec : ICursorTokenCodec
{
    private const int CurrentVersion = 1;
    private const int MaxCursorLength = 256;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string Encode(CursorPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        if (position.Id == 0)
        {
            throw new DomainValidationException("Cursor id must be greater than zero.");
        }

        var normalizedUtc = TimestampPrecision.ToUtcMicroseconds(position.CreatedAtUtc);
        var payload = new CursorPayload(CurrentVersion, normalizedUtc.Ticks, position.Id);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        return ToBase64Url(jsonBytes);
    }

    public CursorPosition Decode(string cursorToken)
    {
        if (string.IsNullOrWhiteSpace(cursorToken) || cursorToken.Length > MaxCursorLength)
        {
            throw new DomainValidationException("Invalid cursor format.");
        }

        byte[] rawBytes;
        try
        {
            rawBytes = FromBase64Url(cursorToken.Trim());
        }
        catch (FormatException)
        {
            throw new DomainValidationException("Invalid cursor format.");
        }

        CursorPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CursorPayload>(rawBytes, SerializerOptions);
        }
        catch (JsonException)
        {
            throw new DomainValidationException("Invalid cursor format.");
        }

        if (payload is null ||
            payload.V != CurrentVersion ||
            payload.I == 0 ||
            payload.T < DateTime.UnixEpoch.Ticks ||
            payload.T > DateTime.MaxValue.Ticks ||
            payload.T % 10 != 0)
        {
            throw new DomainValidationException("Invalid cursor format.");
        }

        var createdAtUtc = new DateTime(payload.T, DateTimeKind.Utc);
        return new CursorPosition(createdAtUtc, payload.I);
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static byte[] FromBase64Url(string token)
    {
        // Reject raw standard Base64 padding/characters so tokens remain strictly canonical Base64url.
        if (token.Contains('+') || token.Contains('/') || token.Contains('='))
        {
            throw new FormatException("Cursor contains non-Base64url characters.");
        }

        var padded = new StringBuilder(token.Length + 2);
        padded.Append(token);
        padded.Replace('-', '+').Replace('_', '/');

        switch (padded.Length % 4)
        {
            case 0:
                break;
            case 2:
                padded.Append("==");
                break;
            case 3:
                padded.Append('=');
                break;
            default:
                throw new FormatException("Invalid Base64url length.");
        }

        return Convert.FromBase64String(padded.ToString());
    }

    private sealed record CursorPayload(int V, long T, ulong I);
}
