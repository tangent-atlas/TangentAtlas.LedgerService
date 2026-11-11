using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations
{
    /// <inheritdoc />
    public partial class Models : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TargetWeight",
                schema: "dbo");

            migrationBuilder.AddColumn<Guid>(
                name: "ModelId",
                schema: "dbo",
                table: "Account",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Model",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Model", x => new { x.TenantId, x.ModelId });
                });

            migrationBuilder.CreateTable(
                name: "Weight",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fraction = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Weight", x => new { x.TenantId, x.ModelId, x.AssetId });
                });

            // Every account has a model, and the ledger refuses one that refers to a model that doesn't exist.  Accounts from
            // before models get one per tenant, with no weights (which never rebalances anything).
            migrationBuilder.Sql("""
                INSERT INTO [dbo].[Model] ([TenantId], [ModelId], [Name], [RowVersion])
                SELECT [TenantId], NEWID(), N'No weights', 0
                FROM (SELECT DISTINCT [TenantId] FROM [dbo].[Account]) AS [Tenants];

                UPDATE [Account]
                SET [ModelId] = [Model].[ModelId]
                FROM [dbo].[Account] AS [Account]
                JOIN [dbo].[Model] AS [Model] ON [Model].[TenantId] = [Account].[TenantId] AND [Model].[Name] = N'No weights';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Model",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Weight",
                schema: "dbo");

            migrationBuilder.DropColumn(
                name: "ModelId",
                schema: "dbo",
                table: "Account");

            migrationBuilder.CreateTable(
                name: "TargetWeight",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false),
                    Weight = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TargetWeight", x => new { x.TenantId, x.AccountId, x.EffectiveDate, x.AssetId });
                });
        }
    }
}
