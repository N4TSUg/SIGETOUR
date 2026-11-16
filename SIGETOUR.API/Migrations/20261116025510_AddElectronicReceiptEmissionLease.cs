using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class AddElectronicReceiptEmissionLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != null && migrationBuilder.ActiveProvider.Contains("Npgsql", System.StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
                    ALTER TABLE ""ElectronicReceipts"" ADD COLUMN IF NOT EXISTS ""EmissionLeaseAcquiredAtUtc"" timestamp with time zone;
                    ALTER TABLE ""ElectronicReceipts"" ADD COLUMN IF NOT EXISTS ""EmissionLeaseExpiresAtUtc"" timestamp with time zone;
                    ALTER TABLE ""ElectronicReceipts"" ADD COLUMN IF NOT EXISTS ""EmissionLeaseToken"" character varying(64);
                    ALTER TABLE ""ElectronicReceipts"" ADD COLUMN IF NOT EXISTS ""EmissionStartedAtUtc"" timestamp with time zone;
                ");
            }
            else
            {
                migrationBuilder.AddColumn<DateTime>(
                    name: "EmissionLeaseAcquiredAtUtc",
                    table: "ElectronicReceipts",
                    type: "timestamp with time zone",
                    nullable: true);

                migrationBuilder.AddColumn<DateTime>(
                    name: "EmissionLeaseExpiresAtUtc",
                    table: "ElectronicReceipts",
                    type: "timestamp with time zone",
                    nullable: true);

                migrationBuilder.AddColumn<string>(
                    name: "EmissionLeaseToken",
                    table: "ElectronicReceipts",
                    type: "character varying(64)",
                    maxLength: 64,
                    nullable: true);

                migrationBuilder.AddColumn<DateTime>(
                    name: "EmissionStartedAtUtc",
                    table: "ElectronicReceipts",
                    type: "timestamp with time zone",
                    nullable: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != null && migrationBuilder.ActiveProvider.Contains("Npgsql", System.StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
                    ALTER TABLE ""ElectronicReceipts"" DROP COLUMN IF EXISTS ""EmissionLeaseAcquiredAtUtc"";
                    ALTER TABLE ""ElectronicReceipts"" DROP COLUMN IF EXISTS ""EmissionLeaseExpiresAtUtc"";
                    ALTER TABLE ""ElectronicReceipts"" DROP COLUMN IF EXISTS ""EmissionLeaseToken"";
                    ALTER TABLE ""ElectronicReceipts"" DROP COLUMN IF EXISTS ""EmissionStartedAtUtc"";
                ");
            }
            else
            {
                migrationBuilder.DropColumn(
                    name: "EmissionLeaseAcquiredAtUtc",
                    table: "ElectronicReceipts");

                migrationBuilder.DropColumn(
                    name: "EmissionLeaseExpiresAtUtc",
                    table: "ElectronicReceipts");

                migrationBuilder.DropColumn(
                    name: "EmissionLeaseToken",
                    table: "ElectronicReceipts");

                migrationBuilder.DropColumn(
                    name: "EmissionStartedAtUtc",
                    table: "ElectronicReceipts");
            }
        }
    }
}
