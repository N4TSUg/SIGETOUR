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
                        v_target_rel_oid oid;
                        v_ref_rel_oid oid;
                        v_target_schema text;
                        v_ref_schema text;
                        v_con record;
                        v_orphans_count integer;
                        v_fk_name text := 'FK_ElectronicReceipts_CashTransactions_CashTransactionId';
                    BEGIN
                        -- 1. Resolver los OIDs reales de ElectronicReceipts y CashTransactions en el search_path activo
                        v_target_rel_oid := to_regclass('""ElectronicReceipts""');
                        v_ref_rel_oid := to_regclass('""CashTransactions""');

                        IF v_target_rel_oid IS NULL THEN
                            RAISE EXCEPTION 'Migración abortada: No se encontró la tabla ""ElectronicReceipts"" en el search_path activo. Se requiere que la tabla exista para verificar o crear la clave foránea.';
                        END IF;

                        IF v_ref_rel_oid IS NULL THEN
                            RAISE EXCEPTION 'Migración abortada: No se encontró la tabla ""CashTransactions"" en el search_path activo. Se requiere que la tabla referenciada exista para verificar o crear la clave foránea.';
                        END IF;

                        -- 2. Obtener por separado el esquema real de cada tabla
                        SELECT n.nspname INTO v_target_schema
                        FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE c.oid = v_target_rel_oid;

                        SELECT n.nspname INTO v_ref_schema
                        FROM pg_class c
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE c.oid = v_ref_rel_oid;

                        -- 3. Buscar si la restricción ya existe específicamente en la tabla objetivo (por conrelid y nombre)
                        SELECT c.conname, c.contype, c.conrelid, c.confrelid, c.confdeltype, c.convalidated,
                               (SELECT attname FROM pg_attribute WHERE attrelid = c.conrelid AND attnum = c.conkey[1]) AS from_col,
                               (SELECT attname FROM pg_attribute WHERE attrelid = c.confrelid AND attnum = c.confkey[1]) AS to_col,
                               array_length(c.conkey, 1) AS from_col_count,
                               array_length(c.confkey, 1) AS to_col_count
                        INTO v_con
                        FROM pg_constraint c
                        WHERE c.conrelid = v_target_rel_oid
                          AND c.conname = v_fk_name;

                        IF FOUND THEN
                            -- 4. Validar compatibilidad exacta de la definición
                            IF NOT (v_con.contype = 'f' 
                               AND v_con.confrelid = v_ref_rel_oid 
                               AND v_con.from_col = 'CashTransactionId' 
                               AND v_con.to_col = 'Id' 
                               AND v_con.from_col_count = 1 
                               AND v_con.to_col_count = 1 
                               AND v_con.confdeltype = 'n') THEN
                                RAISE EXCEPTION 'Migración abortada: La restricción % en %.ElectronicReceipts existe pero tiene una definición incompatible (tipo=%, ref_oid=%, from_col=%, to_col=%, on_delete=%). Se requiere revisión manual de auditoría para no alterar la integridad.',
                                    v_fk_name, v_target_schema, v_con.contype, v_con.confrelid, v_con.from_col, v_con.to_col, v_con.confdeltype;
                            END IF;

                            -- 5. Si ya existe y es compatible, verificar si está validada
                            IF v_con.convalidated THEN
                                RAISE NOTICE 'La restricción % ya existe, su definición es correcta y está validada en %.ElectronicReceipts.', v_fk_name, v_target_schema;
                                RETURN;
                            ELSE
                                -- Restricción NOT VALID: validar ausencia de huérfanos antes de validar
                                EXECUTE format('
                                    SELECT COUNT(*)
                                    FROM %I.""ElectronicReceipts"" r
                                    WHERE r.""CashTransactionId"" IS NOT NULL
                                      AND NOT EXISTS (
                                          SELECT 1 FROM %I.""CashTransactions"" ct WHERE ct.""Id"" = r.""CashTransactionId""
                                      )', v_target_schema, v_ref_schema)
                                INTO v_orphans_count;

                                IF v_orphans_count > 0 THEN
                                    RAISE EXCEPTION 'Migración abortada: La restricción % en %.ElectronicReceipts existe como NOT VALID pero existen % registros con CashTransactionId huérfano. Se requiere intervención manual para resolver los huérfanos antes de validar.',
                                        v_fk_name, v_target_schema, v_orphans_count;
                                END IF;

                                EXECUTE format('
                                    ALTER TABLE %I.""ElectronicReceipts""
                                    VALIDATE CONSTRAINT %I', v_target_schema, v_fk_name);
                                RAISE NOTICE 'La restricción % en %.ElectronicReceipts fue validada exitosamente.', v_fk_name, v_target_schema;
                                RETURN;
                            END IF;
                        END IF;

                        -- 6. Si no existe la restricción: comprobar huérfanos antes de crearla
                        EXECUTE format('
                            SELECT COUNT(*)
                            FROM %I.""ElectronicReceipts"" r
                            WHERE r.""CashTransactionId"" IS NOT NULL
                              AND NOT EXISTS (
                                  SELECT 1 FROM %I.""CashTransactions"" ct WHERE ct.""Id"" = r.""CashTransactionId""
                              )', v_target_schema, v_ref_schema)
                        INTO v_orphans_count;

                        IF v_orphans_count > 0 THEN
                            RAISE EXCEPTION 'Migración abortada: Existen % registros en %.ElectronicReceipts con CashTransactionId huérfano (no existe en CashTransactions). Se requiere intervención manual para no perder trazabilidad.',
                                v_orphans_count, v_target_schema;
                        END IF;

                        -- 7. Crear la clave foránea en la tabla y esquema objetivo
                        EXECUTE format('
                            ALTER TABLE %I.""ElectronicReceipts""
                            ADD CONSTRAINT %I
                            FOREIGN KEY (""CashTransactionId"")
                            REFERENCES %I.""CashTransactions"" (""Id"")
                            ON DELETE SET NULL', v_target_schema, v_fk_name, v_ref_schema);
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
