using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class AddIsActiveToFeaturedPackage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "FeaturedPackages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "FeaturedPackages");
        }
    }
}
