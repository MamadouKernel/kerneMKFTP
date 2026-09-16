using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KernelMK.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialAllowedRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedRolesCsv",
                table: "Credentials",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowedRolesCsv",
                table: "Credentials");
        }
    }
}
