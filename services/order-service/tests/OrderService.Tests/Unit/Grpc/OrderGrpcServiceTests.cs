using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MiniK8s.Contracts.Order.V1;
using MiniK8s.Contracts.Webhook.V1;
using Moq;
using NUnit.Framework;
using OrderService.Application.Abstractions;
using OrderService.Application.Dtos;
using OrderService.Application.Options;
using OrderService.Domain.Exceptions;
using OrderService.Grpc.Extensions;
using OrderService.Grpc.Health;
using OrderService.Grpc.Interceptors;
using OrderService.Grpc.Services;
using OrderService.Infrastructure.Grpc;
using DomainOrderStatus = OrderService.Domain.Enums.OrderStatus;
using ProtoOrderStatus = MiniK8s.Contracts.Order.V1.OrderStatus;

namespace OrderService.Tests.Unit.Grpc;

[TestFixture]
[Category("Unit")]
public sealed class OrderGrpcServiceTests
{
    private Mock<IOrderApplicationService> _appServiceMock = null!;
    private OrderGrpcService _sut = null!;
    private TestServerCallContext _context = null!;

    private static readonly DateTime FixedUtc = new(2026, 11, 23, 15, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        _appServiceMock = new Mock<IOrderApplicationService>();
        _sut = new OrderGrpcService(_appServiceMock.Object);
        _context = new TestServerCallContext(CancellationToken.None);
    }

    [Test]
    public async Task CreateOrder_HappyPath_MapsRequestAndResponse()
    {
        var orderCode = Guid.NewGuid().ToString("D");
        _appServiceMock
            .Setup(x => x.CreateOrderAsync(
                It.Is<CreateOrderCommand>(c =>
                    c.IdempotencyKey == "idem-1" &&
                    c.CustomerId == "cust-1" &&
                    c.Sku == "SKU-1" &&
                    c.Quantity == 2 &&
                    c.UnitPrice == 49.99m &&
                    c.CallbackUrl == "http://mock-webhook:8090/callback" &&
                    c.SimulateWorkMs == 15),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateOrderResult(orderCode, DomainOrderStatus.Pending, false, FixedUtc));

        var response = await _sut.CreateOrder(new CreateOrderRequest
        {
            IdempotencyKey = "idem-1",
            CustomerId = "cust-1",
            Sku = "SKU-1",
            Quantity = 2,
            UnitPrice = "49.99",
            CallbackUrl = "http://mock-webhook:8090/callback",
            SimulateWorkMs = 15
        }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(response.OrderCode, Is.EqualTo(orderCode));
            Assert.That(response.Status, Is.EqualTo(ProtoOrderStatus.Pending));
            Assert.That(response.Deduplicated, Is.False);
            Assert.That(response.CreatedAtUnixMs, Is.EqualTo(new DateTimeOffset(FixedUtc).ToUnixTimeMilliseconds()));
        });
    }

    [Test]
    public void CreateOrder_InvalidUnitPriceOrDomainErrors_ThrowsExpectedRpcStatus()
    {
        var exInvalidPrice = Assert.ThrowsAsync<RpcException>(() => _sut.CreateOrder(new CreateOrderRequest
        {
            IdempotencyKey = "idem-1",
            CustomerId = "cust-1",
            Sku = "SKU-1",
            Quantity = 1,
            UnitPrice = "not-a-decimal"
        }, _context));
        Assert.That(exInvalidPrice!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));

        _appServiceMock
            .Setup(x => x.CreateOrderAsync(It.IsAny<CreateOrderCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConcurrencyConflictException("conflict"));

        var exConflict = Assert.ThrowsAsync<RpcException>(() => _sut.CreateOrder(new CreateOrderRequest
        {
            IdempotencyKey = "idem-1",
            CustomerId = "cust-1",
            Sku = "SKU-1",
            Quantity = 1,
            UnitPrice = "10.00"
        }, _context));
        Assert.That(exConflict!.StatusCode, Is.EqualTo(StatusCode.Aborted));
    }

    [Test]
    public async Task GetOrder_FoundAndNotFound_MapsCorrectly()
    {
        var orderCode = Guid.NewGuid().ToString("D");
        _appServiceMock
            .Setup(x => x.GetOrderAsync(orderCode, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderDetailsDto(
                orderCode, "cust-1", "SKU-1", 3, 10.5m, 31.5m, DomainOrderStatus.Confirmed, null, FixedUtc, FixedUtc.AddSeconds(2)));

        var response = await _sut.GetOrder(new GetOrderRequest { OrderCode = orderCode }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(response.Order.OrderCode, Is.EqualTo(orderCode));
            Assert.That(response.Order.Status, Is.EqualTo(ProtoOrderStatus.Confirmed));
            Assert.That(response.Order.UnitPrice, Is.EqualTo("10.50"));
            Assert.That(response.Order.TotalAmount, Is.EqualTo("31.50"));
            Assert.That(response.Order.CallbackUrl, Is.EqualTo(string.Empty));
        });

        _appServiceMock
            .Setup(x => x.GetOrderAsync("missing", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OrderNotFoundException("missing"));

        var notFound = Assert.ThrowsAsync<RpcException>(() => _sut.GetOrder(new GetOrderRequest { OrderCode = "missing" }, _context));
        Assert.That(notFound!.StatusCode, Is.EqualTo(StatusCode.NotFound));
    }

    [Test]
    public async Task ListOrders_MapsResponseAndErrors()
    {
        var item = new OrderSummaryDto(
            Guid.NewGuid().ToString("D"),
            "cust-1",
            "SKU-1",
            2,
            20m,
            40m,
            DomainOrderStatus.Failed,
            FixedUtc);

        _appServiceMock
            .Setup(x => x.ListOrdersAsync(It.IsAny<ListOrdersQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListOrdersResult([item], "next-tok", "prev-tok", true, true));

        var response = await _sut.ListOrders(new ListOrdersRequest { PageSize = 10 }, _context);

        Assert.Multiple(() =>
        {
            Assert.That(response.Items, Has.Count.EqualTo(1));
            Assert.That(response.Items[0].Status, Is.EqualTo(ProtoOrderStatus.Failed));
            Assert.That(response.Items[0].TotalAmount, Is.EqualTo("40.00"));
            Assert.That(response.NextCursor, Is.EqualTo("next-tok"));
            Assert.That(response.PrevCursor, Is.EqualTo("prev-tok"));
            Assert.That(response.HasNext, Is.True);
            Assert.That(response.HasPrev, Is.True);
        });

        _appServiceMock
            .Setup(x => x.ListOrdersAsync(It.IsAny<ListOrdersQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("query timeout"));

        var timeout = Assert.ThrowsAsync<RpcException>(() => _sut.ListOrders(new ListOrdersRequest { PageSize = 10 }, _context));
        Assert.That(timeout!.StatusCode, Is.EqualTo(StatusCode.DeadlineExceeded));
    }

    [Test]
    public async Task GrpcExceptionInterceptor_MapsDomainAndUnexpectedExceptions()
    {
        var interceptor = new GrpcExceptionInterceptor(NullLogger<GrpcExceptionInterceptor>.Instance);

        var ok = await interceptor.UnaryServerHandler(
            "req",
            _context,
            static (r, _) => Task.FromResult("ok:" + r));
        Assert.That(ok, Is.EqualTo("ok:req"));

        var validationEx = Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<string, string>(
            "req",
            _context,
            static (_, _) => throw new DomainValidationException("bad input")));
        Assert.That(validationEx!.StatusCode, Is.EqualTo(StatusCode.InvalidArgument));

        var internalEx = Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<string, string>(
            "req",
            _context,
            static (_, _) => throw new InvalidOperationException("secret db detail")));
        Assert.Multiple(() =>
        {
            Assert.That(internalEx!.StatusCode, Is.EqualTo(StatusCode.Internal));
            Assert.That(internalEx.Status.Detail, Does.Not.Contain("secret"));
        });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelledContext = new TestServerCallContext(cts.Token);
        var cancelledEx = Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler<string, string>(
            "req",
            cancelledContext,
            static (_, ctx) => throw new OperationCanceledException(ctx.CancellationToken)));
        Assert.That(cancelledEx!.StatusCode, Is.EqualTo(StatusCode.Cancelled));
    }

    [Test]
    public void ReadinessState_TransitionsCleanlyForGracefulShutdown()
    {
        var readiness = new ReadinessState();
        Assert.That(readiness.IsReady, Is.False);
        Assert.That(readiness.StatusLabel, Is.EqualTo("starting"));

        readiness.MarkReady();
        Assert.That(readiness.IsReady, Is.True);
        Assert.That(readiness.StatusLabel, Is.EqualTo("ready"));

        readiness.MarkDraining();
        Assert.That(readiness.IsReady, Is.False);
        Assert.That(readiness.StatusLabel, Is.EqualTo("draining"));

        // MarkReady must not undo draining during SIGTERM shutdown.
        readiness.MarkReady();
        Assert.That(readiness.IsReady, Is.False);
    }

    [Test]
    public async Task GrpcWebhookDispatcher_SuccessAndRpcError_ReturnsStructuredResult()
    {
        var clientMock = new Mock<WebhookService.WebhookServiceClient>();
        var call = new AsyncUnaryCall<SendOrderCallbackResponse>(
            Task.FromResult(new SendOrderCallbackResponse { Delivered = true, HttpStatusCode = 200, Attempts = 1, ErrorMessage = string.Empty }),
            Task.FromResult(new Metadata()),
            static () => Status.DefaultSuccess,
            static () => new Metadata(),
            static () => { });

        clientMock
            .Setup(x => x.SendOrderCallbackAsync(
                It.IsAny<SendOrderCallbackRequest>(),
                null,
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(call);

        var dispatcher = new GrpcWebhookDispatcher(
            clientMock.Object,
            Microsoft.Extensions.Options.Options.Create(new WebhookClientOptions { Address = "http://localhost:5002", TimeoutSeconds = 5 }),
            TimeProvider.System);

        var result = await dispatcher.SendOrderCallbackAsync(new WebhookCallbackRequest(
            "code-1", "CONFIRMED", "http://mock-webhook:8090/callback", "corr-1", 123456));

        Assert.Multiple(() =>
        {
            Assert.That(result.Delivered, Is.True);
            Assert.That(result.HttpStatusCode, Is.EqualTo(200));
            Assert.That(result.Attempts, Is.EqualTo(1));
        });

        clientMock
            .Setup(x => x.SendOrderCallbackAsync(
                It.IsAny<SendOrderCallbackRequest>(),
                null,
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new RpcException(new Status(StatusCode.Unavailable, "webhook down")));

        var failed = await dispatcher.SendOrderCallbackAsync(new WebhookCallbackRequest(
            "code-1", "CONFIRMED", "http://mock-webhook:8090/callback", "corr-1", 123456));

        Assert.Multiple(() =>
        {
            Assert.That(failed.Delivered, Is.False);
            Assert.That(failed.ErrorMessage, Does.Contain("Unavailable"));
        });
    }

    [Test]
    public void DependencyInjectionExtensions_BuildAndValidateScopesWithoutLiveDatabase()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderDb"] = "Server=127.0.0.1;Port=3306;Database=mini_k8s_local;User=minik8s;Password=test;",
                ["Kafka:BootstrapServers"] = "127.0.0.1:19092",
                ["Kafka:Topic"] = "order.created",
                ["Kafka:ConsumerGroupId"] = "order-service-workers",
                ["Kafka:EnableBackgroundWorkers"] = "false",
                ["WebhookClient:Address"] = "http://127.0.0.1:5002",
                ["WebhookClient:TimeoutSeconds"] = "5"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddGlobalServices(config)
            .AddApplicationServices()
            .AddInfrastructureServices(config)
            .AddPresentationServices();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        using var scope = provider.CreateScope();
        Assert.That(scope.ServiceProvider.GetRequiredService<IOrderApplicationService>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<ICursorTokenCodec>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<ReadinessState>(), Is.Not.Null);
    }

    private sealed class TestServerCallContext(CancellationToken ct) : ServerCallContext
    {
        protected override string MethodCore => "/order.v1.OrderService/Test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => ct;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(string.Empty, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
