using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderService.Domain.Entities;

namespace OrderService.Infrastructure.Persistence.Configurations;

public sealed class OrderSummaryViewConfiguration : IEntityTypeConfiguration<OrderSummaryView>
{
    public void Configure(EntityTypeBuilder<OrderSummaryView> builder)
    {
        builder.ToView("vw_order_summary");
        builder.HasNoKey();

        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OrderCode).HasColumnName("order_code");
        builder.Property(x => x.CustomerId).HasColumnName("customer_id");
        builder.Property(x => x.Sku).HasColumnName("sku");
        builder.Property(x => x.Quantity).HasColumnName("quantity");
        builder.Property(x => x.UnitPrice).HasColumnName("unit_price");
        builder.Property(x => x.TotalAmount).HasColumnName("total_amount");
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<byte>();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}
