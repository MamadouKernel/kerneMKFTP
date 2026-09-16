using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KernelMK.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StepExecutionLogs_StartedAt",
                table: "StepExecutionLogs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CreatedAt",
                table: "Notifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_JobTriggers_NextRunAt",
                table: "JobTriggers",
                column: "NextRunAt");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_LastRunAt",
                table: "Jobs",
                column: "LastRunAt");

            migrationBuilder.CreateIndex(
                name: "IX_JobExecutions_StartedAt_Status",
                table: "JobExecutions",
                columns: new[] { "StartedAt", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StepExecutionLogs_StartedAt",
                table: "StepExecutionLogs");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_CreatedAt",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_JobTriggers_NextRunAt",
                table: "JobTriggers");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_LastRunAt",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_JobExecutions_StartedAt_Status",
                table: "JobExecutions");
        }
    }
}
