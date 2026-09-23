using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class SeedTourSlugs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TourPackages",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"),
                column: "Slug",
                value: "ventanillas-de-otuzco");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TourPackages",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"),
                column: "Slug",
                value: "");
        }
    }
}
