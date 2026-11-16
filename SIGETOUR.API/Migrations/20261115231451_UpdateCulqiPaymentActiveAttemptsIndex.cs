using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class UpdateCulqiPaymentActiveAttemptsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Comprobación previa de seguridad en bases de datos relacionales:
            // Validar que no existan múltiples intentos activos que infrinjan el nuevo índice
            // sin eliminar ni alterar silenciosamente ningún registro financiero histórico.
            if (migrationBuilder.ActiveProvider != null && migrationBuilder.ActiveProvider.Contains("Npgsql", System.StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
                    DO $$
                    DECLARE
                        dup_count integer;
                    BEGIN
                        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'CulqiPayments') THEN
                            SELECT COUNT(*) INTO dup_count
                            FROM (
                                SELECT ""BookingId""
                                FROM ""CulqiPayments""
                                WHERE ""Status"" IN ('created', 'processing', 'verification', '3ds_required')
                                GROUP BY ""BookingId""
                                HAVING COUNT(*) > 1
                            ) dups;

                            IF dup_count > 0 THEN
                                RAISE EXCEPTION 'Migración abortada: Existen % reservas con múltiples intentos de pago activos (incluyendo 3ds_required). Se requiere revisión manual de auditoría para no alterar el historial financiero silenciosamente.', dup_count;
                            END IF;
                        END IF;
                    END $$;

                    DROP INDEX IF EXISTS ""IX_CulqiPayments_BookingId"";
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CulqiPayments_BookingId"" ON ""CulqiPayments"" (""BookingId"") WHERE ""Status"" IN ('created', 'processing', 'verification', '3ds_required');
                ");
            }
            else
            {
                migrationBuilder.DropIndex(
                    name: "IX_CulqiPayments_BookingId",
                    table: "CulqiPayments");

                migrationBuilder.CreateIndex(
                    name: "IX_CulqiPayments_BookingId",
                    table: "CulqiPayments",
                    column: "BookingId",
                    unique: true,
                    filter: "\"Status\" IN ('created', 'processing', 'verification', '3ds_required')");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != null && migrationBuilder.ActiveProvider.Contains("Npgsql", System.StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
                    DROP INDEX IF EXISTS ""IX_CulqiPayments_BookingId"";
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CulqiPayments_BookingId"" ON ""CulqiPayments"" (""BookingId"") WHERE ""Status"" IN ('created', 'processing', 'verification');
                ");
            }
            else
            {
                migrationBuilder.DropIndex(
                    name: "IX_CulqiPayments_BookingId",
                    table: "CulqiPayments");

                migrationBuilder.CreateIndex(
                    name: "IX_CulqiPayments_BookingId",
                    table: "CulqiPayments",
                    column: "BookingId",
                    unique: true,
                    filter: "\"Status\" IN ('created', 'processing', 'verification')");
            }
        }
    }
}
