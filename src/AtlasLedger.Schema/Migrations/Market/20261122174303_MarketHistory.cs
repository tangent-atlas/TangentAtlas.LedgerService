using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations.Market;

/// <inheritdoc />
public partial class _20261122174303_MarketHistory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "market");

        migrationBuilder.CreateTable(
            name: "Close",
            schema: "market",
            columns: table => new
            {
                Ticker = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Date = table.Column<DateOnly>(type: "date", nullable: false),
                Close = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Close", x => new { x.Ticker, x.Date });
            });

        migrationBuilder.CreateTable(
            name: "Dividend",
            schema: "market",
            columns: table => new
            {
                Ticker = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ExDate = table.Column<DateOnly>(type: "date", nullable: false),
                PaymentDate = table.Column<DateOnly>(type: "date", nullable: true),
                Amount = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Dividend", x => new { x.Ticker, x.ExDate });
            });

        migrationBuilder.CreateTable(
            name: "Rate",
            schema: "market",
            columns: table => new
            {
                Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Date = table.Column<DateOnly>(type: "date", nullable: false),
                Rate = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Rate", x => new { x.Code, x.Date });
            });

        migrationBuilder.CreateTable(
            name: "Series",
            schema: "market",
            columns: table => new
            {
                Kind = table.Column<int>(type: "int", nullable: false),
                Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                From = table.Column<DateOnly>(type: "date", nullable: false),
                Through = table.Column<DateOnly>(type: "date", nullable: false),
                FetchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Series", x => new { x.Kind, x.Code });
            });

        migrationBuilder.CreateTable(
            name: "Split",
            schema: "market",
            columns: table => new
            {
                Ticker = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Date = table.Column<DateOnly>(type: "date", nullable: false),
                Ratio = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Split", x => new { x.Ticker, x.Date });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Close",
            schema: "market");

        migrationBuilder.DropTable(
            name: "Dividend",
            schema: "market");

        migrationBuilder.DropTable(
            name: "Rate",
            schema: "market");

        migrationBuilder.DropTable(
            name: "Series",
            schema: "market");

        migrationBuilder.DropTable(
            name: "Split",
            schema: "market");
    }
}
