using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtlasLedger.Schema.Migrations
{
    /// <inheritdoc />
    public partial class JobScheduleEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Schedules made before the column existed were running: they stay on.
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                schema: "dbo",
                table: "JobSchedule",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsEnabled",
                schema: "dbo",
                table: "JobSchedule");
        }
    }
}
