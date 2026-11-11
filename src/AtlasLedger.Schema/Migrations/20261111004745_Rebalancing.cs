using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations
{
    /// <inheritdoc />
    public partial class Rebalancing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Fees",
                schema: "dbo",
                table: "Transaction",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CancelRequest",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CancelRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrokerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnteredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CancelRequest", x => new { x.TenantId, x.CancelRequestId });
                });

            migrationBuilder.CreateTable(
                name: "Rebalance",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TradingDay = table.Column<DateOnly>(type: "date", nullable: false),
                    EnteredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    OrderCount = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rebalance", x => new { x.TenantId, x.AccountId, x.TradingDay });
                });

            migrationBuilder.CreateTable(
                name: "RebalanceSetting",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Band = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    Broker = table.Column<int>(type: "int", nullable: false),
                    TimeOfDay = table.Column<int>(type: "int", nullable: false),
                    WeekdaysOnly = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RebalanceSetting", x => new { x.TenantId, x.AccountId });
                });

            migrationBuilder.CreateTable(
                name: "TargetWeight",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Weight = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetWeight", x => new { x.TenantId, x.AccountId, x.EffectiveDate, x.AssetId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CancelRequest",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Rebalance",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "RebalanceSetting",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "TargetWeight",
                schema: "dbo");

            migrationBuilder.DropColumn(
                name: "Fees",
                schema: "dbo",
                table: "Transaction");
        }
    }
}
