using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanctionAndForgive.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListSwitchedOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SwitchedOffAt",
                table: "Sources",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SwitchedOffBy",
                table: "Sources",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SwitchedOffReason",
                table: "Sources",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SwitchedOffAt",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "SwitchedOffBy",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "SwitchedOffReason",
                table: "Sources");
        }
    }
}
