using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderService.Domain.Entities;

namespace OrderService.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasColumnType("bigint")
            .HasConversion<long>()
            .ValueGeneratedOnAdd();

        builder.Property(x => x.OrderId)
            .HasColumnName("order_id")
            .HasColumnType("bigint")
            .HasConversion<long>()
            .IsRequired();

        builder.Property(x => x.EventType)
            .HasColumnName("event_type")
            .HasColumnType("varchar(64)")
            .HasMaxLength(OutboxMessage.MaxEventTypeLength)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(x => x.PartitionKey)
            .HasColumnName("partition_key")
            .HasColumnType("varchar(64)")
            .HasMaxLength(OutboxMessage.MaxPartitionKeyLength)
            .IsUnicode(false)
            .IsRequired();

        builder.Property(x => x.Payload)
            .HasColumnName("payload")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("tinyint")
            .HasConversion<byte>()
            .HasDefaultValueSql("0")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(x => x.RetryCount)
            .HasColumnName("retry_count")
            .HasColumnType("tinyint")
            .HasDefaultValueSql("0")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("datetime2(6)")
            .HasDefaultValueSql("SYSUTCDATETIME()")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(x => x.ProcessedAt)
            .HasColumnName("processed_at")
            .HasColumnType("datetime2(6)")
            .IsRequired(false);

        builder.Ignore(x => x.UpdatedAt);
        builder.Ignore(x => x.RowVersion);
        builder.Ignore(x => x.DomainEvents);

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(x => x.OrderId)
            .HasConstraintName("fk_outbox_order_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.Status, x.CreatedAt, x.Id })
            .HasDatabaseName("ix_outbox_status_created_id");

        builder.HasIndex(x => x.OrderId)
            .HasDatabaseName("ix_outbox_order_id");
    }
}
