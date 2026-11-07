using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.App.Planner.Storage.Migrations
{
    /// <inheritdoc />
    public partial class HolidaysV4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holiday",
                columns: table => new
                {
                    date = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    is_workday = table.Column<bool>(type: "INTEGER", nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holiday", x => x.date);
                });

            migrationBuilder.CreateIndex(
                name: "IX_holiday_date",
                table: "holiday",
                column: "date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holiday");
        }
    }
}
