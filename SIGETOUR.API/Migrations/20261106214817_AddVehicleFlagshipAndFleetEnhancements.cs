using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class AddVehicleFlagshipAndFleetEnhancements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != null && migrationBuilder.ActiveProvider.Contains("Npgsql", System.StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
                    ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""IsFlagship"" boolean NOT NULL DEFAULT FALSE;
                ");
            }
            else
            {
                migrationBuilder.AddColumn<bool>(
                    name: "IsFlagship",
                    table: "Vehicles",
                    type: "boolean",
                    nullable: false,
                    defaultValue: false);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != null && migrationBuilder.ActiveProvider.Contains("Npgsql", System.StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
                    ALTER TABLE ""Vehicles"" DROP COLUMN IF EXISTS ""IsFlagship"";
                ");
            }
            else
            {
                migrationBuilder.DropColumn(
                    name: "IsFlagship",
                    table: "Vehicles");
            }
        }
    }
}
