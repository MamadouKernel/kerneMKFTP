using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KernelMK.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialOAuth2Fields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OAuth2ClientId",
                table: "Credentials",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OAuth2TenantId",
                table: "Credentials",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OAuth2ClientId",
                table: "Credentials");

            migrationBuilder.DropColumn(
                name: "OAuth2TenantId",
                table: "Credentials");
        }
    }
}
