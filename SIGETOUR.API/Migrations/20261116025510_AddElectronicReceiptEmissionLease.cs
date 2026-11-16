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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
