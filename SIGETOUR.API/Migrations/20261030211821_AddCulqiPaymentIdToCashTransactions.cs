using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class AddCulqiPaymentIdToCashTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CulqiPaymentId",
                table: "CashTransactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_CulqiPaymentId",
                table: "CashTransactions",
                column: "CulqiPaymentId",
                unique: true,
                filter: "\"CulqiPaymentId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_CashTransactions_CulqiPayments_CulqiPaymentId",
                table: "CashTransactions",
                column: "CulqiPaymentId",
                principalTable: "CulqiPayments",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashTransactions_CulqiPayments_CulqiPaymentId",
                table: "CashTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CashTransactions_CulqiPaymentId",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "CulqiPaymentId",
                table: "CashTransactions");
        }
    }
}
