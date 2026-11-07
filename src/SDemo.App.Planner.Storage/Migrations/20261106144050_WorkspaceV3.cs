using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.App.Planner.Storage.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "artifacts_json",
                table: "todo",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "completion_note",
                table: "todo",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "completed_at",
                table: "iteration",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "completion_note",
                table: "iteration",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "artifacts_json",
                table: "todo");

            migrationBuilder.DropColumn(
                name: "completion_note",
                table: "todo");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "iteration");

            migrationBuilder.DropColumn(
                name: "completion_note",
                table: "iteration");
        }
    }
}
