using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dbo");

            migrationBuilder.EnsureSchema(
                name: "bellerophon");

            migrationBuilder.CreateTable(
                name: "Account",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountType = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Account", x => new { x.TenantId, x.AccountId });
                });

            migrationBuilder.CreateTable(
                name: "Alert",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlertId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rule = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alert", x => new { x.TenantId, x.AlertId });
                });

            migrationBuilder.CreateTable(
                name: "Dossier",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dossier", x => new { x.TenantId, x.PersonId });
                });

            migrationBuilder.CreateTable(
                name: "Edge",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChildNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentNodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Edge", x => new { x.TenantId, x.ParentNodeId, x.ChildNodeId });
                });

            migrationBuilder.CreateTable(
                name: "Member",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserType = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Member", x => new { x.TenantId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "Node",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NodeStatus = table.Column<int>(type: "int", nullable: false),
                    NodeType = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Node", x => new { x.TenantId, x.NodeId });
                });

            migrationBuilder.CreateTable(
                name: "Person",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Person", x => new { x.TenantId, x.PersonId });
                });

            migrationBuilder.CreateTable(
                name: "Symbol",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Scheme = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Symbol", x => new { x.TenantId, x.Scheme, x.Code });
                });

            migrationBuilder.CreateTable(
                name: "User",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdentityIssuer = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    IdentitySubject = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_User", x => new { x.TenantId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "Watermark",
                schema: "bellerophon",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DurableSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watermark", x => x.TenantId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Account",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Alert",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Dossier",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Edge",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Member",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Node",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Person",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Symbol",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "User",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Watermark",
                schema: "bellerophon");
        }
    }
}
