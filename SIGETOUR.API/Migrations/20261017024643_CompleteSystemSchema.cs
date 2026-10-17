using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class CompleteSystemSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                -- 1. Actualización de columnas en TourPackages
                ALTER TABLE ""TourPackages"" DROP COLUMN IF EXISTS ""Category"";
                ALTER TABLE ""TourPackages"" ADD COLUMN IF NOT EXISTS ""CategoryId"" uuid NULL;

                -- 2. Actualización de columnas en ItineraryStops
                ALTER TABLE ""ItineraryStops"" ADD COLUMN IF NOT EXISTS ""EstimatedTime"" text NULL;

                -- 3. Actualización de columnas en Bookings
                ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""AdditionalPassengersJson"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""CustomerDni"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""InvoiceNumber"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""InvoiceType"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""OperationNumber"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""PaidAmount"" numeric NOT NULL DEFAULT 0;
                ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""PaymentMethod"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""ShiftName"" text NOT NULL DEFAULT '';

                -- 4. Actualización de columnas técnicas en Vehicles
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""Category"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""ChassisNumber"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""CitvExpiryDate"" timestamp with time zone NULL;
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""CitvNumber"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""CitvProvider"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""Color"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""EngineNumber"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""EquipmentJson"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""FuelType"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""ManufactureYear"" integer NOT NULL DEFAULT 0;
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""ResolutionNumber"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""SoatExpiryDate"" timestamp with time zone NULL;
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""SoatIssueDate"" timestamp with time zone NULL;
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""SoatNumber"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""SoatProvider"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""Status"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Vehicles"" ADD COLUMN IF NOT EXISTS ""TucNumber"" text NOT NULL DEFAULT '';

                -- 5. Tabla de Categorías de Tours
                CREATE TABLE IF NOT EXISTS ""Categories"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""Name"" text NOT NULL,
                    ""Description"" text NULL,
                    ""Icon"" text NULL,
                    ""IsActive"" boolean NOT NULL DEFAULT true,
                    ""DisplayOrder"" integer NOT NULL DEFAULT 1,
                    ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                -- 6. Tabla de Paquetes Destacados en Portada
                CREATE TABLE IF NOT EXISTS ""FeaturedPackages"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""TourPackageId"" uuid NOT NULL,
                    ""DisplayOrder"" integer NOT NULL DEFAULT 1,
                    ""CommercialTitle"" text NOT NULL DEFAULT '',
                    ""CommercialSubtitle"" text NOT NULL DEFAULT '',
                    ""PromoBadge"" text NOT NULL DEFAULT '',
                    ""PromoPrice"" numeric NULL,
                    ""SyncInventory"" boolean NOT NULL DEFAULT true
                );

                -- 7. Tabla de Galería de Imágenes de Vehículos
                CREATE TABLE IF NOT EXISTS ""VehicleImages"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""VehicleId"" uuid NOT NULL,
                    ""ImageUrl"" text NOT NULL DEFAULT '',
                    ""IsCover"" boolean NOT NULL DEFAULT false
                );

                -- 8. Módulo de Caja: Turnos de Caja Registradora
                CREATE TABLE IF NOT EXISTS ""CashRegisterShifts"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""ShiftNumber"" text NOT NULL DEFAULT '',
                    ""OpenedByUserName"" text NOT NULL DEFAULT '',
                    ""OpenedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    ""InitialAmount"" numeric NOT NULL DEFAULT 0,
                    ""ClosedByUserName"" text NULL,
                    ""ClosedAt"" timestamp with time zone NULL,
                    ""IsOpen"" boolean NOT NULL DEFAULT true,
                    ""ActualCash"" numeric NULL,
                    ""Notes"" text NULL
                );

                -- 9. Módulo de Caja: Movimientos y Transacciones de Caja
                CREATE TABLE IF NOT EXISTS ""CashTransactions"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""ShiftId"" uuid NOT NULL,
                    ""TransactionType"" text NOT NULL DEFAULT 'Ingreso',
                    ""Category"" text NOT NULL DEFAULT 'Cobro de Reserva',
                    ""Amount"" numeric NOT NULL DEFAULT 0,
                    ""PaymentMethod"" text NOT NULL DEFAULT 'Efectivo',
                    ""ReferenceCode"" text NULL,
                    ""BookingId"" uuid NULL,
                    ""Description"" text NOT NULL DEFAULT '',
                    ""RegisteredBy"" text NOT NULL DEFAULT '',
                    ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                -- 10. Módulo de Caja Chica: Cuentas de Fondo Fijo
                CREATE TABLE IF NOT EXISTS ""PettyCashAccounts"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""Name"" text NOT NULL DEFAULT '',
                    ""TotalFundAmount"" numeric NOT NULL DEFAULT 0,
                    ""ResponsiblePerson"" text NOT NULL DEFAULT '',
                    ""IsActive"" boolean NOT NULL DEFAULT true,
                    ""LastReplenishedAt"" timestamp with time zone NULL
                );

                -- 11. Módulo de Caja Chica: Egresos y Rendiciones
                CREATE TABLE IF NOT EXISTS ""PettyCashMovements"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""AccountId"" uuid NOT NULL,
                    ""MovementType"" text NOT NULL DEFAULT 'Egreso',
                    ""ExpenseCategory"" text NOT NULL DEFAULT 'Combustible',
                    ""VoucherType"" text NOT NULL DEFAULT 'Boleta',
                    ""VoucherNumber"" text NULL,
                    ""Supplier"" text NULL,
                    ""Amount"" numeric NOT NULL DEFAULT 0,
                    ""ResponsiblePerson"" text NOT NULL DEFAULT '',
                    ""Concept"" text NOT NULL DEFAULT '',
                    ""RegisteredAt"" timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                -- 12. Creación segura de Llaves Foráneas (Foreign Keys)
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_TourPackages_Categories_CategoryId') THEN
                        ALTER TABLE ""TourPackages"" ADD CONSTRAINT ""FK_TourPackages_Categories_CategoryId"" FOREIGN KEY (""CategoryId"") REFERENCES ""Categories"" (""Id"") ON DELETE SET NULL;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_FeaturedPackages_TourPackages_TourPackageId') THEN
                        ALTER TABLE ""FeaturedPackages"" ADD CONSTRAINT ""FK_FeaturedPackages_TourPackages_TourPackageId"" FOREIGN KEY (""TourPackageId"") REFERENCES ""TourPackages"" (""Id"") ON DELETE CASCADE;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_VehicleImages_Vehicles_VehicleId') THEN
                        ALTER TABLE ""VehicleImages"" ADD CONSTRAINT ""FK_VehicleImages_Vehicles_VehicleId"" FOREIGN KEY (""VehicleId"") REFERENCES ""Vehicles"" (""Id"") ON DELETE CASCADE;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_CashTransactions_CashRegisterShifts_ShiftId') THEN
                        ALTER TABLE ""CashTransactions"" ADD CONSTRAINT ""FK_CashTransactions_CashRegisterShifts_ShiftId"" FOREIGN KEY (""ShiftId"") REFERENCES ""CashRegisterShifts"" (""Id"") ON DELETE CASCADE;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_CashTransactions_Bookings_BookingId') THEN
                        ALTER TABLE ""CashTransactions"" ADD CONSTRAINT ""FK_CashTransactions_Bookings_BookingId"" FOREIGN KEY (""BookingId"") REFERENCES ""Bookings"" (""Id"") ON DELETE SET NULL;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_PettyCashMovements_PettyCashAccounts_AccountId') THEN
                        ALTER TABLE ""PettyCashMovements"" ADD CONSTRAINT ""FK_PettyCashMovements_PettyCashAccounts_AccountId"" FOREIGN KEY (""AccountId"") REFERENCES ""PettyCashAccounts"" (""Id"") ON DELETE CASCADE;
                    END IF;
                END $$;

                -- 13. Creación de Índices
                CREATE INDEX IF NOT EXISTS ""IX_TourPackages_CategoryId"" ON ""TourPackages"" (""CategoryId"");
                CREATE INDEX IF NOT EXISTS ""IX_CashTransactions_BookingId"" ON ""CashTransactions"" (""BookingId"");
                CREATE INDEX IF NOT EXISTS ""IX_CashTransactions_ShiftId"" ON ""CashTransactions"" (""ShiftId"");
                CREATE INDEX IF NOT EXISTS ""IX_FeaturedPackages_TourPackageId"" ON ""FeaturedPackages"" (""TourPackageId"");
                CREATE INDEX IF NOT EXISTS ""IX_PettyCashMovements_AccountId"" ON ""PettyCashMovements"" (""AccountId"");
                CREATE INDEX IF NOT EXISTS ""IX_VehicleImages_VehicleId"" ON ""VehicleImages"" (""VehicleId"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TourPackages_Categories_CategoryId",
                table: "TourPackages");

            migrationBuilder.DropTable(
                name: "CashTransactions");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "FeaturedPackages");

            migrationBuilder.DropTable(
                name: "PettyCashMovements");

            migrationBuilder.DropTable(
                name: "VehicleImages");

            migrationBuilder.DropTable(
                name: "CashRegisterShifts");

            migrationBuilder.DropTable(
                name: "PettyCashAccounts");

            migrationBuilder.DropIndex(
                name: "IX_TourPackages_CategoryId",
                table: "TourPackages");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "ChassisNumber",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "CitvExpiryDate",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "CitvNumber",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "CitvProvider",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "EngineNumber",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "EquipmentJson",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "FuelType",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "ManufactureYear",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "ResolutionNumber",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "SoatExpiryDate",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "SoatIssueDate",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "SoatNumber",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "SoatProvider",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "TucNumber",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "TourPackages");

            migrationBuilder.DropColumn(
                name: "EstimatedTime",
                table: "ItineraryStops");

            migrationBuilder.DropColumn(
                name: "AdditionalPassengersJson",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CustomerDni",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "InvoiceNumber",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "InvoiceType",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "OperationNumber",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PaidAmount",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "ShiftName",
                table: "Bookings");

            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "TourPackages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "TourPackages",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"),
                column: "Category",
                value: 1);

            migrationBuilder.UpdateData(
                table: "TourPackages",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"),
                column: "Category",
                value: 4);

            migrationBuilder.UpdateData(
                table: "TourPackages",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333333"),
                column: "Category",
                value: 5);
        }
    }
}
