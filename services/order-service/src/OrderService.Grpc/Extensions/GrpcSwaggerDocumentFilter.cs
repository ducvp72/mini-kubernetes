using Google.Protobuf.Reflection;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using ProtoOrderService = MiniK8s.Contracts.Order.V1.OrderService;

namespace OrderService.Grpc.Extensions;

// Pattern: Protobuf Descriptor-Driven OpenAPI Filter — reflects OrderService.Descriptor at runtime so
// Swagger UI always displays exact gRPC RPC paths, request/response schemas, enums, and JSON templates
// without modifying proto/ contracts.
public sealed class GrpcSwaggerDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(swaggerDoc);
        ArgumentNullException.ThrowIfNull(context);

        swaggerDoc.Components ??= new OpenApiComponents();
        swaggerDoc.Components.Schemas ??= new Dictionary<string, OpenApiSchema>(StringComparer.Ordinal);
        swaggerDoc.Paths ??= new OpenApiPaths();

        var serviceDescriptor = ProtoOrderService.Descriptor;
        var tagName = serviceDescriptor.FullName;

        foreach (var method in serviceDescriptor.Methods)
        {
            RegisterMessageSchema(swaggerDoc.Components.Schemas, method.InputType);
            RegisterMessageSchema(swaggerDoc.Components.Schemas, method.OutputType);

            var rpcPath = $"/{serviceDescriptor.FullName}/{method.Name}";
            var operation = new OpenApiOperation
            {
                Tags = [new OpenApiTag { Name = tagName }],
                Summary = $"gRPC {method.Name}",
                Description = $"gRPC unary RPC `{rpcPath}` (invoke over HTTP/2 h2c on gRPC port; JSON body template shown below for Postman / grpcurl).",
                OperationId = $"{serviceDescriptor.Name}_{method.Name}",
                RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = $"Protobuf `{method.InputType.FullName}` request payload.",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.Schema,
                                    Id = method.InputType.Name
                                }
                            },
                            Example = BuildMessageExample(method.InputType)
                        }
                    }
                },
                Responses = new OpenApiResponses
                {
                    ["200"] = new OpenApiResponse
                    {
                        Description = $"Protobuf `{method.OutputType.FullName}` response payload.",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType
                            {
                                Schema = new OpenApiSchema
                                {
                                    Reference = new OpenApiReference
                                    {
                                        Type = ReferenceType.Schema,
                                        Id = method.OutputType.Name
                                    }
                                },
                                Example = BuildMessageExample(method.OutputType)
                            }
                        }
                    }
                }
            };

            swaggerDoc.Paths[rpcPath] = new OpenApiPathItem
            {
                Operations = new Dictionary<OperationType, OpenApiOperation>
                {
                    [OperationType.Post] = operation
                }
            };
        }
    }

    private static void RegisterMessageSchema(
        IDictionary<string, OpenApiSchema> schemas,
        MessageDescriptor message)
    {
        if (schemas.ContainsKey(message.Name))
        {
            return;
        }

        var schema = new OpenApiSchema
        {
            Type = "object",
            Description = $"Protobuf message `{message.FullName}`",
            Properties = new Dictionary<string, OpenApiSchema>(StringComparer.Ordinal)
        };

        // Insert early to guard against recursive message graphs.
        schemas[message.Name] = schema;

        foreach (var field in message.Fields.InFieldNumberOrder())
        {
            schema.Properties[field.JsonName] = BuildFieldSchema(schemas, field);
        }
    }

    private static void RegisterEnumSchema(
        IDictionary<string, OpenApiSchema> schemas,
        EnumDescriptor enumDescriptor)
    {
        if (schemas.ContainsKey(enumDescriptor.Name))
        {
            return;
        }

        schemas[enumDescriptor.Name] = new OpenApiSchema
        {
            Type = "string",
            Description = $"Protobuf enum `{enumDescriptor.FullName}`",
            Enum = enumDescriptor.Values
                .Select(static v => (IOpenApiAny)new OpenApiString(v.Name))
                .ToList()
        };
    }

    private static OpenApiSchema BuildFieldSchema(
        IDictionary<string, OpenApiSchema> schemas,
        FieldDescriptor field)
    {
        var itemSchema = BuildScalarOrCompositeSchema(schemas, field);
        if (field.IsRepeated)
        {
            return new OpenApiSchema
            {
                Type = "array",
                Items = itemSchema
            };
        }

        return itemSchema;
    }

    private static OpenApiSchema BuildScalarOrCompositeSchema(
        IDictionary<string, OpenApiSchema> schemas,
        FieldDescriptor field)
    {
        switch (field.FieldType)
        {
            case FieldType.Message:
                RegisterMessageSchema(schemas, field.MessageType);
                return new OpenApiSchema
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.Schema,
                        Id = field.MessageType.Name
                    }
                };
            case FieldType.Enum:
                RegisterEnumSchema(schemas, field.EnumType);
                return new OpenApiSchema
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.Schema,
                        Id = field.EnumType.Name
                    }
                };
            case FieldType.Bool:
                return new OpenApiSchema { Type = "boolean" };
            case FieldType.Int32:
            case FieldType.SInt32:
            case FieldType.SFixed32:
            case FieldType.UInt32:
            case FieldType.Fixed32:
                return new OpenApiSchema { Type = "integer", Format = "int32" };
            case FieldType.Int64:
            case FieldType.SInt64:
            case FieldType.SFixed64:
            case FieldType.UInt64:
            case FieldType.Fixed64:
                return new OpenApiSchema { Type = "integer", Format = "int64" };
            case FieldType.Float:
                return new OpenApiSchema { Type = "number", Format = "float" };
            case FieldType.Double:
                return new OpenApiSchema { Type = "number", Format = "double" };
            default:
                return new OpenApiSchema { Type = "string" };
        }
    }

    private static OpenApiObject BuildMessageExample(MessageDescriptor message)
    {
        var obj = new OpenApiObject();
        foreach (var field in message.Fields.InFieldNumberOrder())
        {
            var value = BuildFieldExample(field);
            obj[field.JsonName] = field.IsRepeated ? new OpenApiArray { value } : value;
        }

        return obj;
    }

    private static IOpenApiAny BuildFieldExample(FieldDescriptor field)
    {
        if (field.FieldType == FieldType.Message)
        {
            return BuildMessageExample(field.MessageType);
        }

        if (field.FieldType == FieldType.Enum)
        {
            var preferred = field.EnumType.Values.FirstOrDefault(static v => v.Number != 0)
                ?? field.EnumType.Values[0];
            return new OpenApiString(preferred.Name);
        }

        return field.JsonName switch
        {
            "idempotencyKey" => new OpenApiString("idem-0001"),
            "customerId" => new OpenApiString("cust-1001"),
            "orderCode" => new OpenApiString("ord-01936a1b-7c4d-7000-8000-000000000001"),
            "sku" => new OpenApiString("SKU-PROMO-01"),
            "quantity" => new OpenApiInteger(2),
            "unitPrice" => new OpenApiString("49.99"),
            "totalAmount" => new OpenApiString("99.98"),
            "callbackUrl" => new OpenApiString("http://mock-webhook:8090/callback"),
            "simulateWorkMs" => new OpenApiInteger(0),
            "pageSize" => new OpenApiInteger(20),
            "nextCursor" => new OpenApiString(""),
            "prevCursor" => new OpenApiString(""),
            "deduplicated" => new OpenApiBoolean(false),
            "hasNext" => new OpenApiBoolean(true),
            "hasPrev" => new OpenApiBoolean(false),
            "createdAtUnixMs" or "updatedAtUnixMs" => new OpenApiLong(1764000000000),
            _ => field.FieldType switch
            {
                FieldType.Bool => new OpenApiBoolean(false),
                FieldType.Int32 or FieldType.UInt32 or FieldType.SInt32 or FieldType.Fixed32 or FieldType.SFixed32 => new OpenApiInteger(0),
                FieldType.Int64 or FieldType.UInt64 or FieldType.SInt64 or FieldType.Fixed64 or FieldType.SFixed64 => new OpenApiLong(0),
                FieldType.Float or FieldType.Double => new OpenApiDouble(0),
                _ => new OpenApiString("string")
            }
        };
    }
}
