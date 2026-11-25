FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution, project files, and shared proto contracts first for deterministic restore.
COPY services/order-service/OrderService.sln services/order-service/
COPY services/order-service/src/ services/order-service/src/
COPY proto/ proto/

# Ensure any host build artifacts never contaminate the Linux container restore/publish.
RUN rm -rf services/order-service/src/*/bin services/order-service/src/*/obj \
    && dotnet restore services/order-service/src/OrderService.Grpc/OrderService.Grpc.csproj \
    && dotnet publish services/order-service/src/OrderService.Grpc/OrderService.Grpc.csproj \
       -c Release \
       -o /app/publish \
       --no-restore

# Use glibc-based Debian runtime so Confluent.Kafka's native librdkafka works cleanly on arm64 and x86_64.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .
COPY services/order-service/config/ /app/config/

USER app

ENV APP_ENV=local \
    ASPNETCORE_ENVIRONMENT=Production \
    Grpc__Port=5001 \
    Health__Port=8081

EXPOSE 5001 8081

ENTRYPOINT ["dotnet", "OrderService.Grpc.dll"]
