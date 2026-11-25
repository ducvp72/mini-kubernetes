using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderService.Domain.Entities;

namespace OrderService.Infrastructure.Persistence.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", table =>
        {
            table.HasCheckConstraint("chk_orders_quantity_positive", "quantity > 0");
            table.HasCheckConstraint("chk_orders_unit_price_non_negative", "unit_price >= 0");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasColumnType("bigint unsigned")
            .ValueGeneratedOnAdd();

        // MaxLength(36) + IsFixedLength() maps to MySQL char(36) without colliding with Pomelo's Guid char(36) mapping.
        builder.Property(x => x.OrderCode)
            .HasColumnName("order_code")
            .HasMaxLength(Order.OrderCodeLength)
            .IsFixedLength()
            .IsRequired();

        builder.Property(x => x.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasColumnType("varchar(128)")
            .HasMaxLength(Order.MaxIdempotencyKeyLength)
            .IsRequired();

        builder.Property(x => x.CustomerId)
            .HasColumnName("customer_id")
            .HasColumnType("varchar(64)")
            .HasMaxLength(Order.MaxCustomerIdLength)
            .IsRequired();

        builder.Property(x => x.Sku)
            .HasColumnName("sku")
            .HasColumnType("varchar(64)")
            .HasMaxLength(Order.MaxSkuLength)
            .IsRequired();

        builder.Property(x => x.Quantity)
            .HasColumnName("quantity")
            .HasColumnType("int unsigned")
            .IsRequired();

        builder.Property(x => x.UnitPrice)
            .HasColumnName("unit_price")
            .HasColumnType("decimal(18,2)")
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("tinyint unsigned")
            .HasConversion<byte>()
            .HasDefaultValueSql("1")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(x => x.CallbackUrl)
            .HasColumnName("callback_url")
            .HasColumnType("varchar(512)")
            .HasMaxLength(Order.MaxCallbackUrlLength)
            .IsRequired(false);

        builder.Property(x => x.SimulateWorkMs)
            .HasColumnName("simulate_work_ms")
            .HasColumnType("int unsigned")
            .HasDefaultValueSql("0")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(x => x.RowVersion)
            .HasColumnName("row_version")
            .HasColumnType("bigint unsigned")
            .HasDefaultValueSql("1")
            .ValueGeneratedNever()
            .IsConcurrencyToken()
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(x => x.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("datetime(6)")
            .HasDefaultValueSql("CURRENT_TIMESTAMP(6)")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Ignore(x => x.TotalAmount);
        builder.Ignore(x => x.DomainEvents);

        builder.HasIndex(x => x.OrderCode)
            .IsUnique()
            .HasDatabaseName("uq_orders_order_code");

        builder.HasIndex(x => x.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("uq_orders_idempotency_key");

        builder.HasIndex(x => new { x.CreatedAt, x.Id })
            .IsDescending(true, true)
            .HasDatabaseName("ix_orders_created_id");

        builder.HasIndex(x => new { x.CustomerId, x.CreatedAt, x.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_orders_customer_created_id");
    }
}
