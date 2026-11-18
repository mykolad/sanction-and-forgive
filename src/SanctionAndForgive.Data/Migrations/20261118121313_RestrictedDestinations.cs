using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanctionAndForgive.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestrictedDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RestrictedDestinations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OtherNames = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CountryCode = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: true),
                    ParentCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    NeedsRegion = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    RestrictedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LegalSource = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    AsOf = table.Column<DateOnly>(type: "date", nullable: false),
                    ReviewBy = table.Column<DateOnly>(type: "date", nullable: false),
                    AddedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RemovedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RemovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestrictedDestinations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RestrictedDestinations_Code",
                table: "RestrictedDestinations",
                column: "Code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RestrictedDestinations");
        }
    }
}
