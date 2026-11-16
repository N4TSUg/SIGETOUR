using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class AddElectronicReceiptCashTransactionAndBillableKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != null && migrationBuilder.ActiveProvider.Contains("Npgsql", System.StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
                    ALTER TABLE ""ElectronicReceipts"" ADD COLUMN IF NOT EXISTS ""BillableOperationKey"" character varying(128);
                    ALTER TABLE ""ElectronicReceipts"" ADD COLUMN IF NOT EXISTS ""CashTransactionId"" uuid;
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ElectronicReceipts_BillableOperationKey"" ON ""ElectronicReceipts"" (""BillableOperationKey"") WHERE ""BillableOperationKey"" IS NOT NULL;
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ElectronicReceipts_CashTransactionId"" ON ""ElectronicReceipts"" (""CashTransactionId"") WHERE ""CashTransactionId"" IS NOT NULL;
                    DO $$
                    BEGIN
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_ElectronicReceipts_CashTransactions_CashTransactionId') THEN
                            ALTER TABLE ""ElectronicReceipts"" ADD CONSTRAINT ""FK_ElectronicReceipts_CashTransactions_CashTransactionId"" FOREIGN KEY (""CashTransactionId"") REFERENCES ""CashTransactions"" (""Id"") ON DELETE SET NULL;
                        END IF;
                    END $$;
                ");
            }
            else
            {
                migrationBuilder.AddColumn<string>(
                    name: "BillableOperationKey",
                    table: "ElectronicReceipts",
                    type: "character varying(128)",
                    maxLength: 128,
                    nullable: true);

                migrationBuilder.AddColumn<Guid>(
                    name: "CashTransactionId",
                    table: "ElectronicReceipts",
                    type: "uuid",
                    nullable: true);

                migrationBuilder.CreateIndex(
                    name: "IX_ElectronicReceipts_BillableOperationKey",
                    table: "ElectronicReceipts",
                    column: "BillableOperationKey",
                    unique: true,
                    filter: "\"BillableOperationKey\" IS NOT NULL");

                migrationBuilder.CreateIndex(
                    name: "IX_ElectronicReceipts_CashTransactionId",
                    table: "ElectronicReceipts",
                    column: "CashTransactionId",
                    unique: true,
                    filter: "\"CashTransactionId\" IS NOT NULL");

                migrationBuilder.AddForeignKey(
                    name: "FK_ElectronicReceipts_CashTransactions_CashTransactionId",
                    table: "ElectronicReceipts",
                    column: "CashTransactionId",
                    principalTable: "CashTransactions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != null && migrationBuilder.ActiveProvider.Contains("Npgsql", System.StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
                    ALTER TABLE ""ElectronicReceipts"" DROP CONSTRAINT IF EXISTS ""FK_ElectronicReceipts_CashTransactions_CashTransactionId"";
                    DROP INDEX IF EXISTS ""IX_ElectronicReceipts_BillableOperationKey"";
                    DROP INDEX IF EXISTS ""IX_ElectronicReceipts_CashTransactionId"";
                    ALTER TABLE ""ElectronicReceipts"" DROP COLUMN IF EXISTS ""BillableOperationKey"";
                    ALTER TABLE ""ElectronicReceipts"" DROP COLUMN IF EXISTS ""CashTransactionId"";
                ");
            }
            else
            {
                migrationBuilder.DropForeignKey(
                    name: "FK_ElectronicReceipts_CashTransactions_CashTransactionId",
                    table: "ElectronicReceipts");

                migrationBuilder.DropIndex(
                    name: "IX_ElectronicReceipts_BillableOperationKey",
                    table: "ElectronicReceipts");

                migrationBuilder.DropIndex(
                    name: "IX_ElectronicReceipts_CashTransactionId",
                    table: "ElectronicReceipts");

                migrationBuilder.DropColumn(
                    name: "BillableOperationKey",
                    table: "ElectronicReceipts");

                migrationBuilder.DropColumn(
                    name: "CashTransactionId",
                    table: "ElectronicReceipts");
            }
        }
    }
}
