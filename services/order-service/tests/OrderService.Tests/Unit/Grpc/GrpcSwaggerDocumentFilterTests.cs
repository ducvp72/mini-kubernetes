using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi.Models;
using NUnit.Framework;
using OrderService.Grpc.Extensions;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OrderService.Tests.Unit.Grpc;

[TestFixture]
[Category("Unit")]
public sealed class GrpcSwaggerDocumentFilterTests
{
    [Test]
    public void Apply_EmitsAllOrderServiceRpcsAndProtobufSchemas()
    {
        var doc = new OpenApiDocument
        {
            Info = new OpenApiInfo { Title = "Order Service gRPC", Version = "v1" },
            Paths = new OpenApiPaths(),
            Components = new OpenApiComponents()
        };

        var schemaRepo = new SchemaRepository();
        var context = new DocumentFilterContext(Array.Empty<ApiDescription>(), null!, schemaRepo);
        var filter = new GrpcSwaggerDocumentFilter();

        filter.Apply(doc, context);

        Assert.That(doc.Paths.ContainsKey("/order.v1.OrderService/CreateOrder"), Is.True);
        Assert.That(doc.Paths.ContainsKey("/order.v1.OrderService/GetOrder"), Is.True);
        Assert.That(doc.Paths.ContainsKey("/order.v1.OrderService/ListOrders"), Is.True);

        Assert.That(doc.Components.Schemas.ContainsKey("CreateOrderRequest"), Is.True);
        Assert.That(doc.Components.Schemas.ContainsKey("CreateOrderResponse"), Is.True);
        Assert.That(doc.Components.Schemas.ContainsKey("GetOrderRequest"), Is.True);
        Assert.That(doc.Components.Schemas.ContainsKey("GetOrderResponse"), Is.True);
        Assert.That(doc.Components.Schemas.ContainsKey("ListOrdersRequest"), Is.True);
        Assert.That(doc.Components.Schemas.ContainsKey("ListOrdersResponse"), Is.True);
        Assert.That(doc.Components.Schemas.ContainsKey("OrderItem"), Is.True);
        Assert.That(doc.Components.Schemas.ContainsKey("OrderSummary"), Is.True);
        Assert.That(doc.Components.Schemas.ContainsKey("OrderStatus"), Is.True);

        var createOp = doc.Paths["/order.v1.OrderService/CreateOrder"].Operations[OperationType.Post];
        Assert.That(createOp.RequestBody.Content["application/json"].Example, Is.Not.Null);
        Assert.That(createOp.Responses["200"].Content["application/json"].Example, Is.Not.Null);
    }
}
