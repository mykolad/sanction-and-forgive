using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanctionAndForgive.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportHistoryAndReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Imports_SourceId",
                table: "Imports");

            migrationBuilder.AddColumn<bool>(
                name: "HasData",
                table: "Imports",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Until now only the active import and the one before it were kept, both Completed, both with their records.
            migrationBuilder.Sql("UPDATE Imports SET HasData = 1 WHERE Status = 'Completed'");
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                table: "Imports",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedBy",
                table: "Imports",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Imports_SourceId_Status",
                table: "Imports",
                columns: new[] { "SourceId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The earlier code knows no PendingReview: a held import becomes Rejected (its records go with the next cleanup).
            migrationBuilder.Sql("UPDATE Imports SET Status = 'Rejected' WHERE Status = 'PendingReview'");
            migrationBuilder.DropIndex(
                name: "IX_Imports_SourceId_Status",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "HasData",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "Imports");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                table: "Imports");

            migrationBuilder.CreateIndex(
                name: "IX_Imports_SourceId",
                table: "Imports",
                column: "SourceId");
        }
    }
}
