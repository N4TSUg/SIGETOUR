using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class SeedRemainingSlugs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TourPackages",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"),
                column: "Slug",
                value: "collpa");

            migrationBuilder.UpdateData(
                table: "TourPackages",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333333"),
                column: "Slug",
                value: "castillo-de-yanamarca");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "TourPackages",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"),
                column: "Slug",
                value: "");

            migrationBuilder.UpdateData(
                table: "TourPackages",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333333"),
                column: "Slug",
                value: "");
        }
    }
}
