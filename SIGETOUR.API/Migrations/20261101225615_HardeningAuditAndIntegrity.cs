using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class HardeningAuditAndIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ElectronicReceipts_CulqiPaymentId",
                table: "ElectronicReceipts");

            migrationBuilder.DropIndex(
                name: "IX_ElectronicReceipts_Series_Number_ReceiptType",
                table: "ElectronicReceipts");

            migrationBuilder.AddColumn<string>(
                name: "Environment",
                table: "ElectronicReceipts",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Demo");

            migrationBuilder.AddColumn<string>(
                name: "SnapshotJson",
                table: "ElectronicReceipts",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentIncidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BookingId = table.Column<Guid>(type: "uuid", nullable: true),
                    CulqiPaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "PEN"),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "PendingReview"),
                    ResolutionNotes = table.Column<string>(type: "text", nullable: true),
                    ResolvedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentIncidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentIncidents_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PaymentIncidents_CulqiPayments_CulqiPaymentId",
                        column: x => x.CulqiPaymentId,
                        principalTable: "CulqiPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PaymentWebhookEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OrderState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RawPayload = table.Column<string>(type: "text", nullable: false),
                    Processed = table.Column<bool>(type: "boolean", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentWebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicReceipts_CulqiPaymentId",
                table: "ElectronicReceipts",
                column: "CulqiPaymentId",
                unique: true,
                filter: "\"CulqiPaymentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicReceipts_Series_Number_ReceiptType",
                table: "ElectronicReceipts",
                columns: new[] { "Series", "Number", "ReceiptType" },
                unique: true,
                filter: "\"Number\" IS NOT NULL AND \"Number\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_ReferenceCode",
                table: "Bookings",
                column: "ReferenceCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentIncidents_BookingId",
                table: "PaymentIncidents",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentIncidents_CulqiPaymentId",
                table: "PaymentIncidents",
                column: "CulqiPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentIncidents_Status",
                table: "PaymentIncidents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentWebhookEvents_Processed",
                table: "PaymentWebhookEvents",
                column: "Processed");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentWebhookEvents_ProviderId",
                table: "PaymentWebhookEvents",
                column: "ProviderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentIncidents");

            migrationBuilder.DropTable(
                name: "PaymentWebhookEvents");

            migrationBuilder.DropIndex(
                name: "IX_ElectronicReceipts_CulqiPaymentId",
                table: "ElectronicReceipts");

            migrationBuilder.DropIndex(
                name: "IX_ElectronicReceipts_Series_Number_ReceiptType",
                table: "ElectronicReceipts");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_ReferenceCode",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "Environment",
                table: "ElectronicReceipts");

            migrationBuilder.DropColumn(
                name: "SnapshotJson",
                table: "ElectronicReceipts");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicReceipts_CulqiPaymentId",
                table: "ElectronicReceipts",
                column: "CulqiPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_ElectronicReceipts_Series_Number_ReceiptType",
                table: "ElectronicReceipts",
                columns: new[] { "Series", "Number", "ReceiptType" },
                unique: true);
        }
    }
}
