using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MySqlConnector;

namespace OrderService.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for EF Core tooling without requiring a live MySQL instance.
/// </summary>
public sealed class OrderDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public static readonly MySqlServerVersion MySqlVersion = new(new Version(8, 4, 0));

    /// <summary>
    /// Ensures MySqlConnector treats CHAR(36) columns (such as orders.order_code) as strings rather than System.Guid.
    /// </summary>
    public static string NormalizeConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            GuidFormat = MySqlGuidFormat.None
        };
        return builder.ConnectionString;
    }

    public OrderDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OrderDb")
            ?? "Server=localhost;Port=3306;Database=mini_k8s_local;User=minik8s;Password=minik8spass123;";

        var optionsBuilder = new DbContextOptionsBuilder<OrderDbContext>();
        optionsBuilder.UseMySql(NormalizeConnectionString(connectionString), MySqlVersion);

        return new OrderDbContext(optionsBuilder.Options);
    }
}
