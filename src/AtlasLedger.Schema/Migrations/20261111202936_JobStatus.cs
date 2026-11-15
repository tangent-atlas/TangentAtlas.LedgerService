using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations
{
    /// <inheritdoc />
    public partial class JobStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "TimeZone",
                schema: "dbo",
                table: "JobSchedule",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "CronSchedule",
                schema: "dbo",
                table: "JobSchedule",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "dbo",
                table: "JobSchedule",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Carry each row across: enabled was scheduled (JobStatus.Scheduled = 2); disabled is off (0).  Then drop the old column.
            migrationBuilder.Sql("UPDATE [dbo].[JobSchedule] SET [Status] = CASE WHEN [IsEnabled] = 1 THEN 2 ELSE 0 END;");
            migrationBuilder.DropColumn(
                name: "IsEnabled",
                schema: "dbo",
                table: "JobSchedule");

            migrationBuilder.AddColumn<string>(
                name: "StopSchedule",
                schema: "dbo",
                table: "JobSchedule",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                schema: "dbo",
                table: "JobSchedule");

            migrationBuilder.DropColumn(
                name: "StopSchedule",
                schema: "dbo",
                table: "JobSchedule");

            migrationBuilder.AlterColumn<string>(
                name: "TimeZone",
                schema: "dbo",
                table: "JobSchedule",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CronSchedule",
                schema: "dbo",
                table: "JobSchedule",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                schema: "dbo",
                table: "JobSchedule",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }
    }
}
