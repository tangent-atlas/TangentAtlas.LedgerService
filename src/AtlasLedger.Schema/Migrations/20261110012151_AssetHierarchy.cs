using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations
{
    /// <inheritdoc />
    public partial class AssetHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashEntry",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "OpeningBalance",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "PositionOverride",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Security",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Split",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Trade",
                schema: "dbo");

            migrationBuilder.RenameColumn(
                name: "SecurityId",
                schema: "dbo",
                table: "PriceOverride",
                newName: "AssetId");

            migrationBuilder.CreateTable(
                name: "Asset",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetType = table.Column<int>(type: "int", nullable: false),
                    PriceFactor = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    QuantityFactor = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Asset", x => new { x.TenantId, x.AssetId });
                });

            migrationBuilder.CreateTable(
                name: "Cash",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashType = table.Column<int>(type: "int", nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cash", x => new { x.TenantId, x.AssetId });
                });

            migrationBuilder.CreateTable(
                name: "Crypto",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CryptoType = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Crypto", x => new { x.TenantId, x.AssetId });
                });

            migrationBuilder.CreateTable(
                name: "Equity",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EquityType = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Equity", x => new { x.TenantId, x.AssetId });
                });

            migrationBuilder.CreateTable(
                name: "FixedIncome",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CouponRate = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    FixedIncomeType = table.Column<int>(type: "int", nullable: false),
                    MaturityDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedIncome", x => new { x.TenantId, x.AssetId });
                });

            migrationBuilder.CreateTable(
                name: "Fund",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FundType = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fund", x => new { x.TenantId, x.AssetId });
                });

            migrationBuilder.CreateTable(
                name: "Future",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpirationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FutureType = table.Column<int>(type: "int", nullable: false),
                    UnderlyingAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Future", x => new { x.TenantId, x.AssetId });
                });

            migrationBuilder.CreateTable(
                name: "Option",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExerciseStyle = table.Column<int>(type: "int", nullable: false),
                    ExpirationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OptionRight = table.Column<int>(type: "int", nullable: false),
                    StrikePrice = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    UnderlyingAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Option", x => new { x.TenantId, x.AssetId });
                });

            migrationBuilder.CreateTable(
                name: "Transaction",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    EnteredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ExpiresTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceSequence = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transaction", x => new { x.TenantId, x.TransactionId });
                });

            migrationBuilder.CreateTable(
                name: "TransactionLine",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Effect = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    RelatedAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionLine", x => new { x.TenantId, x.TransactionId, x.LineNumber });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Asset",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Cash",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Crypto",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Equity",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "FixedIncome",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Fund",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Future",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Option",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Transaction",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "TransactionLine",
                schema: "dbo");

            migrationBuilder.RenameColumn(
                name: "AssetId",
                schema: "dbo",
                table: "PriceOverride",
                newName: "SecurityId");

            migrationBuilder.CreateTable(
                name: "CashEntry",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    EffectiveTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    EnteredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    SecurityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceSequence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashEntry", x => new { x.TenantId, x.CashEntryId });
                });

            migrationBuilder.CreateTable(
                name: "OpeningBalance",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpeningBalanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcquiredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    CostBasis = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    EffectiveTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    EnteredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    SecurityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceSequence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpeningBalance", x => new { x.TenantId, x.OpeningBalanceId });
                });

            migrationBuilder.CreateTable(
                name: "PositionOverride",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PositionOverrideId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    EnteredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ExpiresTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    SecurityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceSequence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositionOverride", x => new { x.TenantId, x.PositionOverrideId });
                });

            migrationBuilder.CreateTable(
                name: "Security",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecurityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    SecurityType = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Security", x => new { x.TenantId, x.SecurityId });
                });

            migrationBuilder.CreateTable(
                name: "Split",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SplitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Denominator = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    EffectiveTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    EnteredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    Numerator = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    SecurityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceSequence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Split", x => new { x.TenantId, x.SplitId });
                });

            migrationBuilder.CreateTable(
                name: "Trade",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TradeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    EnteredTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    Fees = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    NetAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    SecurityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Side = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceSequence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trade", x => new { x.TenantId, x.TradeId });
                });
        }
    }
}
