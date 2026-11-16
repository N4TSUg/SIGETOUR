using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIGETOUR.API.Migrations
{
    /// <inheritdoc />
    public partial class VerifyAndHardenElectronicReceiptCashTransactionForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != null && migrationBuilder.ActiveProvider.Contains("Npgsql", System.StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql(@"
                    DO $$
                    DECLARE
                        v_schema text := current_schema();
                        v_target_rel_oid oid;
                        v_ref_rel_oid oid;
                        v_con record;
                        v_orphans_count integer;
                        v_fk_name text := 'FK_ElectronicReceipts_CashTransactions_CashTransactionId';
                    BEGIN
                        -- 1. Obtener OIDs de las tablas dentro del esquema actual
                        SELECT c.oid INTO v_target_rel_oid
                        FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE c.relname = 'ElectronicReceipts' AND n.nspname = v_schema;

                        SELECT c.oid INTO v_ref_rel_oid
                        FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE c.relname = 'CashTransactions' AND n.nspname = v_schema;

                        IF v_target_rel_oid IS NULL OR v_ref_rel_oid IS NULL THEN
                            RETURN;
                        END IF;

                        -- 2. Buscar si la restricción ya existe específicamente en la tabla objetivo del esquema actual
                        SELECT c.conname, c.contype, c.conrelid, c.confrelid, c.confdeltype,
                               (SELECT attname FROM pg_attribute WHERE attrelid = c.conrelid AND attnum = c.conkey[1]) AS from_col,
                               (SELECT attname FROM pg_attribute WHERE attrelid = c.confrelid AND attnum = c.confkey[1]) AS to_col,
                               array_length(c.conkey, 1) AS from_col_count,
                               array_length(c.confkey, 1) AS to_col_count
                        INTO v_con
                        FROM pg_constraint c
                        WHERE c.conrelid = v_target_rel_oid
                          AND c.conname = v_fk_name;

                        IF FOUND THEN
                            -- 3. Si existe, comprobar que su definición sea exactamente la esperada
                            IF v_con.contype = 'f' 
                               AND v_con.confrelid = v_ref_rel_oid 
                               AND v_con.from_col = 'CashTransactionId' 
                               AND v_con.to_col = 'Id' 
                               AND v_con.from_col_count = 1 
                               AND v_con.to_col_count = 1 
                               AND v_con.confdeltype = 'n' THEN
                                RAISE NOTICE 'La restricción % ya existe y su definición es correcta en %.ElectronicReceipts.', v_fk_name, v_schema;
                                RETURN;
                            ELSE
                                RAISE EXCEPTION 'Migración abortada: La restricción % en %.ElectronicReceipts existe pero tiene una definición incompatible (tipo=%, ref_oid=%, from_col=%, to_col=%, on_delete=%). Se requiere revisión manual de auditoría para no alterar la integridad.',
                                    v_fk_name, v_schema, v_con.contype, v_con.confrelid, v_con.from_col, v_con.to_col, v_con.confdeltype;
                            END IF;
                        END IF;

                        -- 4. Comprobar que no existan datos huérfanos antes de crear la restricción
                        EXECUTE format('
                            SELECT COUNT(*)
                            FROM %I.""ElectronicReceipts"" r
                            WHERE r.""CashTransactionId"" IS NOT NULL
                              AND NOT EXISTS (
                                  SELECT 1 FROM %I.""CashTransactions"" ct WHERE ct.""Id"" = r.""CashTransactionId""
                              )', v_schema, v_schema)
                        INTO v_orphans_count;

                        IF v_orphans_count > 0 THEN
                            RAISE EXCEPTION 'Migración abortada: Existen % registros en %.ElectronicReceipts con CashTransactionId huérfano (no existe en CashTransactions). Se requiere intervención manual para no perder trazabilidad.',
                                v_orphans_count, v_schema;
                        END IF;

                        -- 5. Crear la clave foránea en la tabla y esquema objetivo
                        EXECUTE format('
                            ALTER TABLE %I.""ElectronicReceipts""
                            ADD CONSTRAINT %I
                            FOREIGN KEY (""CashTransactionId"")
                            REFERENCES %I.""CashTransactions"" (""Id"")
                            ON DELETE SET NULL', v_schema, v_fk_name, v_schema);
                    END $$;
                ");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Esta migración de auditoría y endurecimiento valida la integridad y garantiza la existencia
            // de la restricción. En Down se preserva la integridad referencial sin eliminarla.
        }
    }
}
