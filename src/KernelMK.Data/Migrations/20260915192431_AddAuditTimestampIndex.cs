using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KernelMK.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditTimestampIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_Timestamp",
                table: "AuditLogEntries",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogEntries_Timestamp",
                table: "AuditLogEntries");
        }
    }
}
