using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class AddTourSlug : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "TourPackages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "TourPackages",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"),
                column: "Slug",
                value: "");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Slug",
                table: "TourPackages");
        }
    }
}
