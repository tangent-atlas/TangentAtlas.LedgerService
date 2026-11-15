using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations
{
    /// <inheritdoc />
    public partial class OptionalModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ModelId",
                schema: "dbo",
                table: "Account",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            // A model now says the account is rebalanced to it, and null that it is not.  The placeholder models the Models
            // migration gave existing accounts would now mean "sell everything", so they go, and their accounts get none.
            migrationBuilder.Sql("""
                UPDATE [Account]
                SET [ModelId] = NULL
                FROM [dbo].[Account] AS [Account]
                JOIN [dbo].[Model] AS [Model] ON [Model].[TenantId] = [Account].[TenantId] AND [Model].[ModelId] = [Account].[ModelId]
                WHERE [Model].[Name] = N'No weights';

                DELETE [Model]
                FROM [dbo].[Model] AS [Model]
                WHERE [Model].[Name] = N'No weights'
                  AND NOT EXISTS (SELECT 1 FROM [dbo].[Weight] AS [Weight] WHERE [Weight].[TenantId] = [Model].[TenantId] AND [Weight].[ModelId] = [Model].[ModelId]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ModelId",
                schema: "dbo",
                table: "Account",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
