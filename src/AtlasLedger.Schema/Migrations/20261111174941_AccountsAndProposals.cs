using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations
{
    /// <inheritdoc />
    public partial class AccountsAndProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountConnection",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "PriceOverride",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Rebalance",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "RebalanceSetting",
                schema: "dbo");

            migrationBuilder.DropColumn(
                name: "Broker",
                schema: "dbo",
                table: "BrokerOrder");

            migrationBuilder.AddColumn<decimal>(
                name: "Band",
                schema: "dbo",
                table: "Model",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: false,
                defaultValue: 0.005m);   // models before bands get the Joe 10's, half a percent

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                schema: "dbo",
                table: "BrokerOrder",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "AccountRole",
                schema: "dbo",
                table: "Account",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ExternalAccountId",
                schema: "dbo",
                table: "Account",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InstitutionId",
                schema: "dbo",
                table: "Account",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Brokerage",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrokerageType = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Brokerage", x => new { x.TenantId, x.AccountId });
                });

            migrationBuilder.CreateTable(
                name: "Institution",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstitutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Institution", x => new { x.TenantId, x.InstitutionId });
                });

            // Account.AccountType used to say an account's place in an adviser's tree; that is AccountRole now, and
            // AccountType says what kind of account it is.  Every account so far is a brokerage account (0).
            migrationBuilder.Sql("""
                UPDATE [dbo].[Account] SET [AccountRole] = [AccountType], [AccountType] = 0;
                """);

            // A broker order now names the account it is placed in: until now an order's broker orders were for its one
            // account, the account of its allocation.
            migrationBuilder.Sql("""
                UPDATE [BrokerOrder]
                SET [AccountId] = [Allocation].[AccountId]
                FROM [dbo].[BrokerOrder] AS [BrokerOrder]
                CROSS APPLY (SELECT TOP (1) [AccountId] FROM [dbo].[Allocation]
                             WHERE [Allocation].[TenantId] = [BrokerOrder].[TenantId] AND [Allocation].[OrderId] = [BrokerOrder].[OrderId]
                             ORDER BY [AccountId]) AS [Allocation];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Brokerage",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Institution",
                schema: "dbo");

            migrationBuilder.DropColumn(
                name: "Band",
                schema: "dbo",
                table: "Model");

            migrationBuilder.DropColumn(
                name: "AccountId",
                schema: "dbo",
                table: "BrokerOrder");

            migrationBuilder.DropColumn(
                name: "AccountRole",
                schema: "dbo",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "ExternalAccountId",
                schema: "dbo",
                table: "Account");

            migrationBuilder.DropColumn(
                name: "InstitutionId",
                schema: "dbo",
                table: "Account");

            migrationBuilder.AddColumn<int>(
                name: "Broker",
                schema: "dbo",
                table: "BrokerOrder",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AccountConnection",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalAccountId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountConnection", x => new { x.TenantId, x.AccountConnectionId });
                });

            migrationBuilder.CreateTable(
                name: "PriceOverride",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PriceOverrideId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    EnteredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ExpiresTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceSequence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceOverride", x => new { x.TenantId, x.PriceOverrideId });
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
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    TimeOfDay = table.Column<int>(type: "int", nullable: false),
                    WeekdaysOnly = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RebalanceSetting", x => new { x.TenantId, x.AccountId });
                });
        }
    }
}
