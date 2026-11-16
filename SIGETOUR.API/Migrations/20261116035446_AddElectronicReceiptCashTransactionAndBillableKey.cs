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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
