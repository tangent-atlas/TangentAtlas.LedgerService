using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations;

/// <inheritdoc />
public partial class _20261120035912_Portfolios : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropPrimaryKey(
            name: "PK_Edge",
            schema: "dbo",
            table: "Edge");

        migrationBuilder.DropColumn(
            name: "AccountRole",
            schema: "dbo",
            table: "Account");

        migrationBuilder.DropColumn(
            name: "ModelId",
            schema: "dbo",
            table: "Account");

        migrationBuilder.AddColumn<decimal>(
            name: "CashAmount",
            schema: "dbo",
            table: "Model",
            type: "decimal(19,4)",
            precision: 19,
            scale: 4,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CashRule",
            schema: "dbo",
            table: "Model",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "ModelKind",
            schema: "dbo",
            table: "Model",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<decimal>(
            name: "VolatilityBand",
            schema: "dbo",
            table: "Model",
            type: "decimal(9,6)",
            precision: 9,
            scale: 6,
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "VolatilityTarget",
            schema: "dbo",
            table: "Model",
            type: "decimal(9,6)",
            precision: 9,
            scale: 6,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "VolatilityWindow",
            schema: "dbo",
            table: "Model",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Position",
            schema: "dbo",
            table: "Edge",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateOnly>(
            name: "Since",
            schema: "dbo",
            table: "Edge",
            type: "date",
            nullable: false,
            defaultValue: new DateOnly(1, 1, 1));

        migrationBuilder.AddPrimaryKey(
            name: "PK_Edge",
            schema: "dbo",
            table: "Edge",
            columns: new[] { "TenantId", "ChildNodeId" });

        migrationBuilder.CreateTable(
            name: "CustomListMember",
            schema: "dbo",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Scheme = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Fraction = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                RowVersion = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CustomListMember", x => new { x.TenantId, x.ModelId, x.Scheme, x.Code });
            });

        migrationBuilder.CreateTable(
            name: "CustomListModel",
            schema: "dbo",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IsConstantWeight = table.Column<bool>(type: "bit", nullable: false),
                ListWeighting = table.Column<int>(type: "int", nullable: false),
                RowVersion = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CustomListModel", x => new { x.TenantId, x.ModelId });
            });

        migrationBuilder.CreateTable(
            name: "IndexModel",
            schema: "dbo",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Companies = table.Column<int>(type: "int", nullable: false),
                IndexCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                RowVersion = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IndexModel", x => new { x.TenantId, x.ModelId });
            });

        migrationBuilder.CreateTable(
            name: "Portfolio",
            schema: "dbo",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PortfolioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RowVersion = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Portfolio", x => new { x.TenantId, x.PortfolioId });
            });

        migrationBuilder.CreateTable(
            name: "Restriction",
            schema: "dbo",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                PortfolioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Scheme = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                RowVersion = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Restriction", x => new { x.TenantId, x.PortfolioId, x.Scheme, x.Code });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CustomListMember",
            schema: "dbo");

        migrationBuilder.DropTable(
            name: "CustomListModel",
            schema: "dbo");

        migrationBuilder.DropTable(
            name: "IndexModel",
            schema: "dbo");

        migrationBuilder.DropTable(
            name: "Portfolio",
            schema: "dbo");

        migrationBuilder.DropTable(
            name: "Restriction",
            schema: "dbo");

        migrationBuilder.DropPrimaryKey(
            name: "PK_Edge",
            schema: "dbo",
            table: "Edge");

        migrationBuilder.DropColumn(
            name: "CashAmount",
            schema: "dbo",
            table: "Model");

        migrationBuilder.DropColumn(
            name: "CashRule",
            schema: "dbo",
            table: "Model");

        migrationBuilder.DropColumn(
            name: "ModelKind",
            schema: "dbo",
            table: "Model");

        migrationBuilder.DropColumn(
            name: "VolatilityBand",
            schema: "dbo",
            table: "Model");

        migrationBuilder.DropColumn(
            name: "VolatilityTarget",
            schema: "dbo",
            table: "Model");

        migrationBuilder.DropColumn(
            name: "VolatilityWindow",
            schema: "dbo",
            table: "Model");

        migrationBuilder.DropColumn(
            name: "Position",
            schema: "dbo",
            table: "Edge");

        migrationBuilder.DropColumn(
            name: "Since",
            schema: "dbo",
            table: "Edge");

        migrationBuilder.AddColumn<int>(
            name: "AccountRole",
            schema: "dbo",
            table: "Account",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<Guid>(
            name: "ModelId",
            schema: "dbo",
            table: "Account",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.AddPrimaryKey(
            name: "PK_Edge",
            schema: "dbo",
            table: "Edge",
            columns: new[] { "TenantId", "ParentNodeId", "ChildNodeId" });
    }
}
