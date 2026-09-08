using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KernelMK.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialSshKeyAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuthType",
                table: "Credentials",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedPassphrase",
                table: "Credentials",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthType",
                table: "Credentials");

            migrationBuilder.DropColumn(
                name: "EncryptedPassphrase",
                table: "Credentials");
        }
    }
}
