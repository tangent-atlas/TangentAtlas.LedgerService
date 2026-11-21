using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations;

/// <inheritdoc />
public partial class _20261121014236_Favorites : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Favorite",
            schema: "dbo",
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PortfolioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Position = table.Column<int>(type: "int", nullable: false),
                RowVersion = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Favorite", x => new { x.TenantId, x.UserId, x.PortfolioId });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Favorite",
            schema: "dbo");
    }
}
