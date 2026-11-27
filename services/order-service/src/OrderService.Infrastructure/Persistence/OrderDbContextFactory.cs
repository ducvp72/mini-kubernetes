using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderService.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for EF Core tooling without requiring a live SQL Server / Azure SQL instance.
/// </summary>
public sealed class OrderDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public OrderDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OrderDb")
            ?? "Server=127.0.0.1,1433;Database=mini_k8s_local;User Id=sa;Password=admin@123456;Encrypt=True;TrustServerCertificate=True;";

        var optionsBuilder = new DbContextOptionsBuilder<OrderDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        return new OrderDbContext(optionsBuilder.Options);
    }
}
