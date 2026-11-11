using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations
{
    /// <inheritdoc />
    public partial class Orders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionReportId",
                schema: "dbo",
                table: "Transaction",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrderId",
                schema: "dbo",
                table: "Transaction",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Allocation",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Allocation", x => new { x.TenantId, x.OrderId, x.AccountId });
                });

            migrationBuilder.CreateTable(
                name: "BrokerOrder",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrokerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Broker = table.Column<int>(type: "int", nullable: false),
                    EnteredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    LimitPrice = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderType = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    TimeInForce = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrokerOrder", x => new { x.TenantId, x.BrokerOrderId });
                });

            migrationBuilder.CreateTable(
                name: "ExecutionReport",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrokerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fees = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Price = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: true),
                    ReportTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ReportType = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionReport", x => new { x.TenantId, x.ExecutionReportId });
                });

            migrationBuilder.CreateTable(
                name: "Order",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnteredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    Origin = table.Column<int>(type: "int", nullable: false),
                    RuleName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Side = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Order", x => new { x.TenantId, x.OrderId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Allocation",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "BrokerOrder",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "ExecutionReport",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Order",
                schema: "dbo");

            migrationBuilder.DropColumn(
                name: "ExecutionReportId",
                schema: "dbo",
                table: "Transaction");

            migrationBuilder.DropColumn(
                name: "OrderId",
                schema: "dbo",
                table: "Transaction");
        }
    }
}
