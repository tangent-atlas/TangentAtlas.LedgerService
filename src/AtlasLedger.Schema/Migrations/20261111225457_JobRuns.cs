using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations
{
    /// <inheritdoc />
    public partial class JobRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobRun",
                schema: "dbo",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EndTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    Job = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    JobScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    RequestedTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    StartTime = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRun", x => new { x.TenantId, x.JobRunId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobRun",
                schema: "dbo");
        }
    }
}
