using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KernelMK.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserNotificationsReadAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NotificationsReadAt",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            // Comptes existants : on considère tout l'historique déjà "lu" à la mise à jour, pour éviter
            // un afflux artificiel de notifications non lues sur des exécutions déjà connues des utilisateurs.
            migrationBuilder.Sql("UPDATE AspNetUsers SET NotificationsReadAt = CURRENT_TIMESTAMP WHERE NotificationsReadAt IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificationsReadAt",
                table: "AspNetUsers");
        }
    }
}
