using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class AddCulqiPaymentsAndElectronicReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""TourPackages"" ADD COLUMN IF NOT EXISTS ""IsFlagship"" boolean NOT NULL DEFAULT false;
                ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""AssignedDriverId"" uuid NULL;
                ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""AssignedVehicleId"" uuid NULL;

                CREATE TABLE IF NOT EXISTS ""CulqiPayments"" (
                    ""Id"" uuid NOT NULL,
                    ""BookingId"" uuid NOT NULL,
                    ""AmountCents"" bigint NOT NULL,
                    ""Channel"" character varying(32) NOT NULL,
                    ""Status"" character varying(32) NOT NULL,
                    ""ProviderId"" character varying(100) NULL,
                    ""CreatedAtUtc"" timestamp with time zone NOT NULL,
                    ""ConfirmedAtUtc"" timestamp with time zone NULL,
                    CONSTRAINT ""PK_CulqiPayments"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""CK_CulqiPayment_Positive"" CHECK (""AmountCents"" > 0),
                    CONSTRAINT ""FK_CulqiPayments_Bookings_BookingId"" FOREIGN KEY (""BookingId"") REFERENCES ""Bookings"" (""Id"") ON DELETE RESTRICT
                );

                CREATE TABLE IF NOT EXISTS ""Drivers"" (
                    ""Id"" uuid NOT NULL,
                    ""FullName"" text NOT NULL,
                    ""Dni"" text NOT NULL,
                    ""LicenseNumber"" text NOT NULL,
                    ""LicenseCategory"" text NOT NULL,
                    ""LicenseExpiryDate"" timestamp with time zone NULL,
                    ""Phone"" text NOT NULL,
                    ""IsActive"" boolean NOT NULL,
                    ""AssignedVehicleId"" uuid NULL,
                    ""CreatedAt"" timestamp with time zone NOT NULL,
                    CONSTRAINT ""PK_Drivers"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_Drivers_Vehicles_AssignedVehicleId"" FOREIGN KEY (""AssignedVehicleId"") REFERENCES ""Vehicles"" (""Id"") ON DELETE SET NULL
                );

                CREATE TABLE IF NOT EXISTS ""TourReviews"" (
                    ""Id"" uuid NOT NULL,
                    ""TourPackageId"" uuid NOT NULL,
                    ""AuthorName"" text NOT NULL,
                    ""AuthorCity"" text NOT NULL,
                    ""Comment"" text NOT NULL,
                    ""CreatedAt"" timestamp with time zone NOT NULL,
                    ""IsApproved"" boolean NOT NULL,
                    ""IsVisibleOnHome"" boolean NOT NULL,
                    CONSTRAINT ""PK_TourReviews"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_TourReviews_TourPackages_TourPackageId"" FOREIGN KEY (""TourPackageId"") REFERENCES ""TourPackages"" (""Id"") ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS ""ElectronicReceipts"" (
                    ""Id"" uuid NOT NULL,
                    ""BookingId"" uuid NOT NULL,
                    ""CulqiPaymentId"" uuid NULL,
                    ""ReceiptType"" character varying(20) NOT NULL,
                    ""Series"" character varying(10) NOT NULL,
                    ""Number"" character varying(20) NOT NULL,
                    ""CustomerDocType"" character varying(10) NOT NULL,
                    ""CustomerDocNumber"" character varying(20) NOT NULL,
                    ""CustomerName"" character varying(200) NOT NULL,
                    ""CustomerEmail"" character varying(150) NOT NULL,
                    ""SubtotalAmount"" numeric(12,2) NOT NULL,
                    ""IgvAmount"" numeric(12,2) NOT NULL,
                    ""TotalAmount"" numeric(12,2) NOT NULL,
                    ""Status"" character varying(30) NOT NULL,
                    ""ProviderResponseCode"" text NULL,
                    ""ProviderDescription"" text NULL,
                    ""PdfUrl"" text NULL,
                    ""XmlUrl"" text NULL,
                    ""CdrUrl"" text NULL,
                    ""QrData"" text NULL,
                    ""DigitalHash"" text NULL,
                    ""LastError"" text NULL,
                    ""CreatedAtUtc"" timestamp with time zone NOT NULL,
                    ""IssuedAtUtc"" timestamp with time zone NULL,
                    CONSTRAINT ""PK_ElectronicReceipts"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_ElectronicReceipts_Bookings_BookingId"" FOREIGN KEY (""BookingId"") REFERENCES ""Bookings"" (""Id"") ON DELETE RESTRICT,
                    CONSTRAINT ""FK_ElectronicReceipts_CulqiPayments_CulqiPaymentId"" FOREIGN KEY (""CulqiPaymentId"") REFERENCES ""CulqiPayments"" (""Id"") ON DELETE SET NULL
                );

                CREATE INDEX IF NOT EXISTS ""IX_Bookings_AssignedDriverId"" ON ""Bookings"" (""AssignedDriverId"");
                CREATE INDEX IF NOT EXISTS ""IX_Bookings_AssignedVehicleId"" ON ""Bookings"" (""AssignedVehicleId"");
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CulqiPayments_BookingId"" ON ""CulqiPayments"" (""BookingId"") WHERE ""Status"" IN ('created', 'processing', 'verification');
                CREATE INDEX IF NOT EXISTS ""IX_CulqiPayments_BookingId_CreatedAtUtc"" ON ""CulqiPayments"" (""BookingId"", ""CreatedAtUtc"");
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CulqiPayments_ProviderId"" ON ""CulqiPayments"" (""ProviderId"");
                CREATE INDEX IF NOT EXISTS ""IX_Drivers_AssignedVehicleId"" ON ""Drivers"" (""AssignedVehicleId"");
                CREATE INDEX IF NOT EXISTS ""IX_ElectronicReceipts_BookingId"" ON ""ElectronicReceipts"" (""BookingId"");
                CREATE INDEX IF NOT EXISTS ""IX_ElectronicReceipts_CulqiPaymentId"" ON ""ElectronicReceipts"" (""CulqiPaymentId"");
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ElectronicReceipts_Series_Number_ReceiptType"" ON ""ElectronicReceipts"" (""Series"", ""Number"", ""ReceiptType"");
                CREATE INDEX IF NOT EXISTS ""IX_TourReviews_TourPackageId"" ON ""TourReviews"" (""TourPackageId"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TABLE IF EXISTS ""ElectronicReceipts"";
            ");
        }
    }
}
