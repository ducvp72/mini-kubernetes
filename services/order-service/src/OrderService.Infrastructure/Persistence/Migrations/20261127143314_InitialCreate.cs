using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    order_code = table.Column<string>(type: "char(36)", unicode: false, fixedLength: true, maxLength: 36, nullable: false),
                    idempotency_key = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    customer_id = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    sku = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    quantity = table.Column<int>(type: "int", nullable: false),
                    unit_price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    status = table.Column<byte>(type: "tinyint", nullable: false, defaultValueSql: "1"),
                    callback_url = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: true),
                    simulate_work_ms = table.Column<int>(type: "int", nullable: false, defaultValueSql: "0"),
                    created_at = table.Column<DateTime>(type: "datetime2(6)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    updated_at = table.Column<DateTime>(type: "datetime2(6)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    row_version = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("chk_orders_quantity_positive", "quantity > 0");
                    table.CheckConstraint("chk_orders_unit_price_non_negative", "unit_price >= 0");
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    order_id = table.Column<long>(type: "bigint", nullable: false),
                    event_type = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    partition_key = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<byte>(type: "tinyint", nullable: false, defaultValueSql: "0"),
                    retry_count = table.Column<byte>(type: "tinyint", nullable: false, defaultValueSql: "0"),
                    processed_at = table.Column<DateTime>(type: "datetime2(6)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(6)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_outbox_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_orders_created_id",
                table: "orders",
                columns: new[] { "created_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_orders_customer_created_id",
                table: "orders",
                columns: new[] { "customer_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "uq_orders_idempotency_key",
                table: "orders",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_orders_order_code",
                table: "orders",
                column: "order_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_order_id",
                table: "outbox_messages",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_status_created_id",
                table: "outbox_messages",
                columns: new[] { "status", "created_at", "id" });

            migrationBuilder.Sql("""
                CREATE OR ALTER VIEW dbo.vw_order_summary AS
                SELECT
                    id,
                    order_code,
                    customer_id,
                    sku,
                    quantity,
                    unit_price,
                    CAST(quantity * unit_price AS DECIMAL(18,2)) AS total_amount,
                    status,
                    created_at
                FROM dbo.orders;
                """);

            migrationBuilder.Sql("""
                CREATE OR ALTER PROCEDURE dbo.sp_claim_outbox_batch
                    @batchSize INT
                AS
                BEGIN
                    SET NOCOUNT ON;

                    SELECT TOP (@batchSize)
                        id,
                        order_id,
                        event_type,
                        partition_key,
                        payload,
                        status,
                        retry_count,
                        created_at,
                        processed_at
                    FROM dbo.outbox_messages WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE status = 0
                    ORDER BY created_at ASC, id ASC;
                END
                """);

            migrationBuilder.Sql("""
                CREATE OR ALTER FUNCTION dbo.fn_order_status_label(@p_status TINYINT)
                RETURNS VARCHAR(16)
                AS
                BEGIN
                    RETURN CASE @p_status
                        WHEN 1 THEN 'PENDING'
                        WHEN 2 THEN 'CONFIRMED'
                        WHEN 3 THEN 'FAILED'
                        ELSE 'UNSPECIFIED'
                    END;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS dbo.fn_order_status_label;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.sp_claim_outbox_batch;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS dbo.vw_order_summary;");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "orders");
        }
    }
}
