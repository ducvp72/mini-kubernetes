using System.Text;
using NUnit.Framework;
using OrderService.Application.Pagination;
using OrderService.Domain.Exceptions;
using OrderService.Domain.Pagination;

namespace OrderService.Tests.Unit.Application;

[TestFixture]
[Category("Unit")]
public sealed class CursorTokenCodecTests
{
    private CursorTokenCodec _codec = null!;

    [SetUp]
    public void SetUp()
    {
        _codec = new CursorTokenCodec();
    }

    [Test]
    public void EncodeAndDecode_PreservesMicrosecondTimestampAndId()
    {
        var timestamp = new DateTime(2026, 11, 23, 14, 22, 10, DateTimeKind.Utc).AddTicks(987_650);
        var position = new CursorPosition(timestamp, 4294967299UL);

        var token = _codec.Encode(position);
        var decoded = _codec.Decode(token);

        Assert.Multiple(() =>
        {
            Assert.That(token, Does.Not.Contain("+").And.Not.Contain("/").And.Not.Contain("="));
            Assert.That(decoded.CreatedAtUtc, Is.EqualTo(timestamp));
            Assert.That(decoded.CreatedAtUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(decoded.Id, Is.EqualTo(4294967299UL));
        });
    }

    [Test]
    public void Encode_NullOrZeroId_ThrowsExpectedException()
    {
        Assert.Throws<ArgumentNullException>(() => _codec.Encode(null!));
        Assert.Throws<DomainValidationException>(() => _codec.Encode(new CursorPosition(DateTime.UtcNow, 0)));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not-valid-base64!!!")]
    [TestCase("abc+def/ghi=")]
    [TestCase("a")]
    public void Decode_MalformedTokens_ThrowsDomainValidationException(string badToken)
    {
        Assert.Throws<DomainValidationException>(() => _codec.Decode(badToken));
    }

    [Test]
    public void Decode_TamperedJsonPayloads_ThrowsDomainValidationException()
    {
        var oversized = new string('a', 300);
        Assert.Throws<DomainValidationException>(() => _codec.Decode(oversized));

        var wrongVersion = ToBase64Url("""{"v":2,"t":639000000000000000,"i":10}""");
        var zeroId = ToBase64Url("""{"v":1,"t":639000000000000000,"i":0}""");
        var preEpochTicks = ToBase64Url("""{"v":1,"t":100,"i":10}""");
        var subMicrosecondTicks = ToBase64Url("""{"v":1,"t":639000000000000007,"i":10}""");
        var extraField = ToBase64Url("""{"v":1,"t":639000000000000000,"i":10,"extra":1}""");
        var notJson = ToBase64Url("hello world");

        Assert.Throws<DomainValidationException>(() => _codec.Decode(wrongVersion));
        Assert.Throws<DomainValidationException>(() => _codec.Decode(zeroId));
        Assert.Throws<DomainValidationException>(() => _codec.Decode(preEpochTicks));
        Assert.Throws<DomainValidationException>(() => _codec.Decode(subMicrosecondTicks));
        Assert.Throws<DomainValidationException>(() => _codec.Decode(extraField));
        Assert.Throws<DomainValidationException>(() => _codec.Decode(notJson));
    }

    private static string ToBase64Url(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
