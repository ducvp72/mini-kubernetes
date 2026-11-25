using Microsoft.EntityFrameworkCore.Metadata;
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
            migrationBuilder.AlterDatabase()
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    order_code = table.Column<string>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    idempotency_key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    customer_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sku = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    quantity = table.Column<uint>(type: "int unsigned", nullable: false),
                    unit_price = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    status = table.Column<byte>(type: "tinyint unsigned", nullable: false, defaultValueSql: "1"),
                    callback_url = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    simulate_work_ms = table.Column<uint>(type: "int unsigned", nullable: false, defaultValueSql: "0"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(6)"),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(6)"),
                    row_version = table.Column<ulong>(type: "bigint unsigned", nullable: false, defaultValueSql: "1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.CheckConstraint("chk_orders_quantity_positive", "quantity > 0");
                    table.CheckConstraint("chk_orders_unit_price_non_negative", "unit_price >= 0");
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<ulong>(type: "bigint unsigned", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    order_id = table.Column<ulong>(type: "bigint unsigned", nullable: false),
                    event_type = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    partition_key = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    payload = table.Column<string>(type: "json", nullable: false, collation: "utf8mb4_0900_ai_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<byte>(type: "tinyint unsigned", nullable: false, defaultValueSql: "0"),
                    retry_count = table.Column<byte>(type: "tinyint unsigned", nullable: false, defaultValueSql: "0"),
                    processed_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(6)")
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
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "ix_orders_created_id",
                table: "orders",
                columns: new[] { "created_at", "id" },
                descending: new[] { true, true });

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
                CREATE OR REPLACE ALGORITHM=MERGE VIEW vw_order_summary AS
                SELECT
                    id,
                    order_code,
                    customer_id,
                    sku,
                    quantity,
                    unit_price,
                    (quantity * unit_price) AS total_amount,
                    status,
                    created_at
                FROM orders;
                """);

            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS sp_claim_outbox_batch;");
            migrationBuilder.Sql("""
                CREATE PROCEDURE sp_claim_outbox_batch(IN p_batch_size INT)
                BEGIN
                    SELECT
                        id,
                        order_id,
                        event_type,
                        partition_key,
                        payload,
                        status,
                        retry_count,
                        created_at,
                        processed_at
                    FROM outbox_messages
                    WHERE status = 0
                    ORDER BY created_at ASC, id ASC
                    LIMIT p_batch_size
                    FOR UPDATE SKIP LOCKED;
                END
                """);

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS fn_order_status_label;");
            migrationBuilder.Sql("""
                CREATE FUNCTION fn_order_status_label(p_status TINYINT UNSIGNED)
                RETURNS VARCHAR(16)
                DETERMINISTIC
                NO SQL
                BEGIN
                    RETURN CASE p_status
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
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS fn_order_status_label;");
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS sp_claim_outbox_batch;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS vw_order_summary;");

            migrationBuilder.DropTable(name: "outbox_messages");
            migrationBuilder.DropTable(name: "orders");
        }
    }
}
