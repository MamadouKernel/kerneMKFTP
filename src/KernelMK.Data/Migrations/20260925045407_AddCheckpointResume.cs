using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KernelMK.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckpointResume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JobDefinitionHash",
                table: "JobExecutions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedJobDefinitionHash",
                table: "JobExecutionRequests",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResumeCompletedStepIdsJson",
                table: "JobExecutionRequests",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "ResumeOfExecutionId",
                table: "JobExecutionRequests",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JobDefinitionHash",
                table: "JobExecutions");

            migrationBuilder.DropColumn(
                name: "ExpectedJobDefinitionHash",
                table: "JobExecutionRequests");

            migrationBuilder.DropColumn(
                name: "ResumeCompletedStepIdsJson",
                table: "JobExecutionRequests");

            migrationBuilder.DropColumn(
                name: "ResumeOfExecutionId",
                table: "JobExecutionRequests");
        }
    }
}
