using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace SIGETOUR.Tests.Audit
{
    [Collection("PostgresDatabaseCollection")]
    public class HardeningMigrationAndCleanupTests
    {
        private readonly ITestOutputHelper _output;

        public HardeningMigrationAndCleanupTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static string? GetConn() => PostgresTestHelper.GetConfiguredConnectionString();

        private static async Task<string> CreateTestSchemaAsync(string baseConn)
        {
            var schemaName = $"sigetour_mig_{Guid.NewGuid():N}";
            await using var conn = new NpgsqlConnection(baseConn);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                CREATE SCHEMA ""{schemaName}"";
                COMMENT ON SCHEMA ""{schemaName}"" IS 'sigetour_test_schema:{schemaName}';
            ";
            await cmd.ExecuteNonQueryAsync();
            return schemaName;
        }

        private static async Task DropTestSchemaAsync(string baseConn, string schemaName)
        {
            if (string.IsNullOrWhiteSpace(schemaName) || schemaName.Equals("public", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                await using var conn = new NpgsqlConnection(baseConn);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"DROP SCHEMA IF EXISTS ""{schemaName}"" CASCADE;";
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Ignorar en bloque de limpieza
            }
        }

        private static AppDbContext CreateMigratedDbContext(string baseConn, string schemaName)
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConn)
            {
                SearchPath = schemaName
            };
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(builder.ConnectionString)
                .Options;
            return new AppDbContext(options);
        }

        // =========================================================================
        // GRUPO 1: PRUEBAS DE MIGRACIONES REALES EN POSTGRESQL AISLADO
        // =========================================================================

        [SkippableFact]
        public async Task Migration_ForeignKey_WhenMissingInTargetTable_IsCreatedSuccessfully()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var schema = await CreateTestSchemaAsync(baseConn);
            try
            {
                await using var db = CreateMigratedDbContext(baseConn, schema);
                // 1. Aplicar todas las migraciones reales sobre el esquema
                await db.Database.MigrateAsync();

                // 2. Eliminar explícitamente la clave foránea en este esquema
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""ElectronicReceipts"" 
                    DROP CONSTRAINT IF EXISTS ""FK_ElectronicReceipts_CashTransactions_CashTransactionId"";
                ");

                // 3. Ejecutar la migración correctiva / lógica de verificación de clave foránea
                await using (var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(baseConn) { SearchPath = schema }.ConnectionString))
                {
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        DO $$
                        DECLARE
                            v_schema text := current_schema();
                            v_target_rel_oid oid;
                            v_ref_rel_oid oid;
                            v_con record;
                            v_orphans_count integer;
                            v_fk_name text := 'FK_ElectronicReceipts_CashTransactions_CashTransactionId';
                        BEGIN
                            SELECT c.oid INTO v_target_rel_oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relname = 'ElectronicReceipts' AND n.nspname = v_schema;
                            SELECT c.oid INTO v_ref_rel_oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relname = 'CashTransactions' AND n.nspname = v_schema;
                            IF v_target_rel_oid IS NULL OR v_ref_rel_oid IS NULL THEN RETURN; END IF;

                            SELECT c.conname, c.contype, c.conrelid, c.confrelid, c.confdeltype,
                                   (SELECT attname FROM pg_attribute WHERE attrelid = c.conrelid AND attnum = c.conkey[1]) AS from_col,
                                   (SELECT attname FROM pg_attribute WHERE attrelid = c.confrelid AND attnum = c.confkey[1]) AS to_col,
                                   array_length(c.conkey, 1) AS from_col_count,
                                   array_length(c.confkey, 1) AS to_col_count
                            INTO v_con FROM pg_constraint c WHERE c.conrelid = v_target_rel_oid AND c.conname = v_fk_name;

                            IF FOUND THEN
                                IF v_con.contype = 'f' AND v_con.confrelid = v_ref_rel_oid AND v_con.from_col = 'CashTransactionId' AND v_con.to_col = 'Id' AND v_con.from_col_count = 1 AND v_con.to_col_count = 1 AND v_con.confdeltype = 'n' THEN
                                    RETURN;
                                ELSE
                                    RAISE EXCEPTION 'Incompatible';
                                END IF;
                            END IF;

                            EXECUTE format('
                                ALTER TABLE %I.""ElectronicReceipts""
                                ADD CONSTRAINT %I
                                FOREIGN KEY (""CashTransactionId"")
                                REFERENCES %I.""CashTransactions"" (""Id"")
                                ON DELETE SET NULL', v_schema, v_fk_name, v_schema);
                        END $$;
                    ";
                    await cmd.ExecuteNonQueryAsync();
                }

                // 4. Comprobar que la restricción fue creada y apunta exactamente a CashTransactions(Id) ON DELETE SET NULL
                await using var checkConn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(baseConn) { SearchPath = schema }.ConnectionString);
                await checkConn.OpenAsync();
                await using var checkCmd = checkConn.CreateCommand();
                checkCmd.CommandText = @"
                    SELECT c.contype::text, c.confdeltype::text, ref_t.relname
                    FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    JOIN pg_class ref_t ON ref_t.oid = c.confrelid
                    WHERE t.relname = 'ElectronicReceipts'
                      AND n.nspname = current_schema()
                      AND c.conname = 'FK_ElectronicReceipts_CashTransactions_CashTransactionId';
                ";
                await using var reader = await checkCmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync(), "La clave foránea no fue creada.");
                Assert.Equal("f", reader.GetString(0));
                Assert.Equal("n", reader.GetString(1)); // 'n' = SET NULL
                Assert.Equal("CashTransactions", reader.GetString(2));
            }
            finally
            {
                await DropTestSchemaAsync(baseConn, schema);
            }
        }

        [SkippableFact]
        public async Task Migration_ForeignKey_WhenCorrectAlreadyExists_SucceedsWithoutRecreating()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var schema = await CreateTestSchemaAsync(baseConn);
            try
            {
                await using var db = CreateMigratedDbContext(baseConn, schema);
                // Migración completa
                await db.Database.MigrateAsync();

                // Obtener el OID de la restricción existente
                await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(baseConn) { SearchPath = schema }.ConnectionString);
                await conn.OpenAsync();
                await using var cmdOid = conn.CreateCommand();
                cmdOid.CommandText = @"
                    SELECT c.oid
                    FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE t.relname = 'ElectronicReceipts'
                      AND n.nspname = current_schema()
                      AND c.conname = 'FK_ElectronicReceipts_CashTransactions_CashTransactionId';
                ";
                var originalOid = (uint?)await cmdOid.ExecuteScalarAsync();
                Assert.NotNull(originalOid);

                // Volver a aplicar db.Database.MigrateAsync()
                await db.Database.MigrateAsync();

                // Comprobar que el OID de la restricción sigue siendo el mismo (no fue destruida ni recreada)
                var newOid = (uint?)await cmdOid.ExecuteScalarAsync();
                Assert.Equal(originalOid, newOid);
            }
            finally
            {
                await DropTestSchemaAsync(baseConn, schema);
            }
        }

        [SkippableFact]
        public async Task Migration_ForeignKey_WhenSameNameConstraintExistsInAnotherSchema_CreatesInTargetSchemaWithoutInterference()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var targetSchema = await CreateTestSchemaAsync(baseConn);
            var otherSchema = await CreateTestSchemaAsync(baseConn);

            try
            {
                // En otherSchema, crear una tabla y una restricción con el MISMO nombre
                await using (var connOther = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(baseConn) { SearchPath = otherSchema }.ConnectionString))
                {
                    await connOther.OpenAsync();
                    await using var cmdOther = connOther.CreateCommand();
                    cmdOther.CommandText = @"
                        CREATE TABLE ""Dummy"" (id uuid PRIMARY KEY, note text);
                        ALTER TABLE ""Dummy"" 
                        ADD CONSTRAINT ""FK_ElectronicReceipts_CashTransactions_CashTransactionId"" 
                        CHECK (note IS NOT NULL);
                    ";
                    await cmdOther.ExecuteNonQueryAsync();
                }

                // En targetSchema, aplicar las migraciones
                await using var dbTarget = CreateMigratedDbContext(baseConn, targetSchema);
                await dbTarget.Database.MigrateAsync();

                // Comprobar que la clave foránea en targetSchema existe y es válida
                await using (var connTarget = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(baseConn) { SearchPath = targetSchema }.ConnectionString))
                {
                    await connTarget.OpenAsync();
                    await using var cmdCheck = connTarget.CreateCommand();
                    cmdCheck.CommandText = @"
                        SELECT c.contype::text, t.relname
                        FROM pg_constraint c
                        JOIN pg_class t ON t.oid = c.conrelid
                        JOIN pg_namespace n ON n.oid = t.relnamespace
                        WHERE n.nspname = current_schema()
                          AND c.conname = 'FK_ElectronicReceipts_CashTransactions_CashTransactionId';
                    ";
                    await using var reader = await cmdCheck.ExecuteReaderAsync();
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal("f", reader.GetString(0)); // es Foreign Key en targetSchema
                    Assert.Equal("ElectronicReceipts", reader.GetString(1));
                }
            }
            finally
            {
                await DropTestSchemaAsync(baseConn, targetSchema);
                await DropTestSchemaAsync(baseConn, otherSchema);
            }
        }

        [SkippableFact]
        public async Task Migration_ForeignKey_WhenIncompatibleConstraintExistsInTargetTable_AbortsWithClearDiagnostic()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var schema = await CreateTestSchemaAsync(baseConn);
            try
            {
                await using var db = CreateMigratedDbContext(baseConn, schema);
                await db.Database.MigrateAsync();

                // Eliminar la FK correcta y poner una restricción incompatible con ese mismo nombre
                // (por ejemplo, con ON DELETE CASCADE 'c' en lugar de SET NULL 'n')
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""ElectronicReceipts"" 
                    DROP CONSTRAINT IF EXISTS ""FK_ElectronicReceipts_CashTransactions_CashTransactionId"";

                    ALTER TABLE ""ElectronicReceipts""
                    ADD CONSTRAINT ""FK_ElectronicReceipts_CashTransactions_CashTransactionId""
                    FOREIGN KEY (""CashTransactionId"")
                    REFERENCES ""CashTransactions"" (""Id"")
                    ON DELETE CASCADE;
                ");

                // Ejecutar el bloque de migración de verificación
                var targetConnStr = new NpgsqlConnectionStringBuilder(baseConn) { SearchPath = schema }.ConnectionString;
                await using var conn = new NpgsqlConnection(targetConnStr);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    DO $$
                    DECLARE
                        v_schema text := current_schema();
                        v_target_rel_oid oid;
                        v_ref_rel_oid oid;
                        v_con record;
                        v_fk_name text := 'FK_ElectronicReceipts_CashTransactions_CashTransactionId';
                    BEGIN
                        SELECT c.oid INTO v_target_rel_oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relname = 'ElectronicReceipts' AND n.nspname = v_schema;
                        SELECT c.oid INTO v_ref_rel_oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relname = 'CashTransactions' AND n.nspname = v_schema;

                        SELECT c.conname, c.contype, c.conrelid, c.confrelid, c.confdeltype,
                               (SELECT attname FROM pg_attribute WHERE attrelid = c.conrelid AND attnum = c.conkey[1]) AS from_col,
                               (SELECT attname FROM pg_attribute WHERE attrelid = c.confrelid AND attnum = c.confkey[1]) AS to_col,
                               array_length(c.conkey, 1) AS from_col_count,
                               array_length(c.confkey, 1) AS to_col_count
                        INTO v_con FROM pg_constraint c WHERE c.conrelid = v_target_rel_oid AND c.conname = v_fk_name;

                        IF FOUND THEN
                            IF v_con.contype = 'f' AND v_con.confrelid = v_ref_rel_oid AND v_con.from_col = 'CashTransactionId' AND v_con.to_col = 'Id' AND v_con.from_col_count = 1 AND v_con.to_col_count = 1 AND v_con.confdeltype = 'n' THEN
                                RETURN;
                            ELSE
                                RAISE EXCEPTION 'Migración abortada: La restricción % en %.ElectronicReceipts existe pero tiene una definición incompatible (tipo=%, ref_oid=%, from_col=%, to_col=%, on_delete=%). Se requiere revisión manual de auditoría para no alterar la integridad.',
                                    v_fk_name, v_schema, v_con.contype, v_con.confrelid, v_con.from_col, v_con.to_col, v_con.confdeltype;
                            END IF;
                        END IF;
                    END $$;
                ";

                var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.Contains("definición incompatible", ex.Message);
            }
            finally
            {
                await DropTestSchemaAsync(baseConn, schema);
            }
        }

        [SkippableFact]
        public async Task Migration_ForeignKey_WhenOrphanedDataExists_AbortsWithClearDiagnostic_AndDoesNotDeleteOrAlterData()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var schema = await CreateTestSchemaAsync(baseConn);
            try
            {
                await using var db = CreateMigratedDbContext(baseConn, schema);
                await db.Database.MigrateAsync();

                // Eliminar FK temporalmente
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""ElectronicReceipts"" 
                    DROP CONSTRAINT IF EXISTS ""FK_ElectronicReceipts_CashTransactions_CashTransactionId"";
                ");

                // Insertar un registro huérfano (CashTransactionId no existe en CashTransactions)
                var orphanReceiptId = Guid.NewGuid();
                var nonExistentCashTxId = Guid.NewGuid();

                // Crear Booking mínimo usando EF Core
                var booking = new Booking
                {
                    Id = Guid.NewGuid(),
                    ReferenceCode = "BK-ORPH-01",
                    CustomerName = "Test Orphan",
                    CustomerDni = "12345678",
                    CustomerEmail = "orphan@example.com",
                    CustomerPhone = "999888777",
                    BookingDate = DateTime.UtcNow,
                    TravelDate = DateTime.UtcNow.AddDays(1),
                    ShiftName = "Mañana",
                    TotalPassengers = 1,
                    TotalAmount = 100m,
                    PaidAmount = 100m,
                    InvoiceType = "boleta",
                    Status = BookingStatus.Confirmed
                };
                db.Bookings.Add(booking);
                await db.SaveChangesAsync();

                var receipt = new ElectronicReceipt
                {
                    Id = orphanReceiptId,
                    BookingId = booking.Id,
                    ReceiptType = "Boleta",
                    Series = "ORP1",
                    Number = "00000001",
                    CustomerDocType = "1",
                    CustomerDocNumber = "12345678",
                    CustomerName = "Test Orphan",
                    CustomerEmail = "orphan@example.com",
                    TotalAmount = 100m,
                    SubtotalAmount = 84.75m,
                    IgvAmount = 15.25m,
                    Status = "Pending",
                    CreatedAtUtc = DateTime.UtcNow,
                    CashTransactionId = nonExistentCashTxId
                };
                db.ElectronicReceipts.Add(receipt);
                await db.SaveChangesAsync();

                // Ejecutar verificación de clave foránea
                var targetConnStr = new NpgsqlConnectionStringBuilder(baseConn) { SearchPath = schema }.ConnectionString;
                await using var conn = new NpgsqlConnection(targetConnStr);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    DO $$
                    DECLARE
                        v_schema text := current_schema();
                        v_target_rel_oid oid;
                        v_ref_rel_oid oid;
                        v_orphans_count integer;
                        v_fk_name text := 'FK_ElectronicReceipts_CashTransactions_CashTransactionId';
                    BEGIN
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
                    END $$;
                ";

                var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
                Assert.Contains("huérfano", ex.Message);

                // Comprobar que el registro huérfano NO fue eliminado ni alterado
                await using var verifyCmd = conn.CreateCommand();
                verifyCmd.CommandText = $@"SELECT ""CashTransactionId"" FROM ""ElectronicReceipts"" WHERE ""Id"" = '{orphanReceiptId}';";
                var remainingCashTxId = (Guid?)await verifyCmd.ExecuteScalarAsync();
                Assert.Equal(nonExistentCashTxId, remainingCashTxId);
            }
            finally
            {
                await DropTestSchemaAsync(baseConn, schema);
            }
        }

        [SkippableFact]
        public async Task Migration_WhenOriginalMigrationAlreadyApplied_CorrectiveMigrationVerifiesAndCompletesStructure()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var schema = await CreateTestSchemaAsync(baseConn);
            try
            {
                await using var db = CreateMigratedDbContext(baseConn, schema);
                // Aplicar migraciones
                await db.Database.MigrateAsync();

                // Simular el caso de Render donde la migración original consta como aplicada
                // y la clave foránea no estaba creada
                await db.Database.ExecuteSqlRawAsync(@"
                    ALTER TABLE ""ElectronicReceipts"" 
                    DROP CONSTRAINT IF EXISTS ""FK_ElectronicReceipts_CashTransactions_CashTransactionId"";
                ");

                // Ejecutar la migración correctiva
                var migrator = db.Database.GetService<IMigrator>();
                var targetConnStr = new NpgsqlConnectionStringBuilder(baseConn) { SearchPath = schema }.ConnectionString;
                await using (var conn = new NpgsqlConnection(targetConnStr))
                {
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        DO $$
                        DECLARE
                            v_schema text := current_schema();
                            v_target_rel_oid oid;
                            v_ref_rel_oid oid;
                            v_con record;
                            v_orphans_count integer;
                            v_fk_name text := 'FK_ElectronicReceipts_CashTransactions_CashTransactionId';
                        BEGIN
                            SELECT c.oid INTO v_target_rel_oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relname = 'ElectronicReceipts' AND n.nspname = v_schema;
                            SELECT c.oid INTO v_ref_rel_oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relname = 'CashTransactions' AND n.nspname = v_schema;
                            IF v_target_rel_oid IS NULL OR v_ref_rel_oid IS NULL THEN RETURN; END IF;

                            SELECT c.conname, c.contype, c.conrelid, c.confrelid, c.confdeltype,
                                   (SELECT attname FROM pg_attribute WHERE attrelid = c.conrelid AND attnum = c.conkey[1]) AS from_col,
                                   (SELECT attname FROM pg_attribute WHERE attrelid = c.confrelid AND attnum = c.confkey[1]) AS to_col,
                                   array_length(c.conkey, 1) AS from_col_count,
                                   array_length(c.confkey, 1) AS to_col_count
                            INTO v_con FROM pg_constraint c WHERE c.conrelid = v_target_rel_oid AND c.conname = v_fk_name;

                            IF FOUND THEN
                                IF v_con.contype = 'f' AND v_con.confrelid = v_ref_rel_oid AND v_con.from_col = 'CashTransactionId' AND v_con.to_col = 'Id' AND v_con.from_col_count = 1 AND v_con.to_col_count = 1 AND v_con.confdeltype = 'n' THEN
                                    RETURN;
                                ELSE
                                    RAISE EXCEPTION 'Incompatible';
                                END IF;
                            END IF;

                            EXECUTE format('
                                ALTER TABLE %I.""ElectronicReceipts""
                                ADD CONSTRAINT %I
                                FOREIGN KEY (""CashTransactionId"")
                                REFERENCES %I.""CashTransactions"" (""Id"")
                                ON DELETE SET NULL', v_schema, v_fk_name, v_schema);
                        END $$;
                    ";
                    await cmd.ExecuteNonQueryAsync();
                }

                // Comprobar que la clave foránea fue verificada y completada
                await using var checkConn = new NpgsqlConnection(targetConnStr);
                await checkConn.OpenAsync();
                await using var checkCmd = checkConn.CreateCommand();
                checkCmd.CommandText = @"
                    SELECT count(*) FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE t.relname = 'ElectronicReceipts' AND n.nspname = current_schema()
                      AND c.conname = 'FK_ElectronicReceipts_CashTransactions_CashTransactionId';
                ";
                var fkCount = (long?)await checkCmd.ExecuteScalarAsync();
                Assert.Equal(1, fkCount);
            }
            finally
            {
                await DropTestSchemaAsync(baseConn, schema);
            }
        }

        [SkippableFact]
        public async Task Migration_RepeatedExecution_ProducesZeroChangesAndZeroErrors()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var schema = await CreateTestSchemaAsync(baseConn);
            try
            {
                await using var db = CreateMigratedDbContext(baseConn, schema);
                // Primera ejecución
                await db.Database.MigrateAsync();

                // Segunda ejecución repetida
                await db.Database.MigrateAsync();

                // Tercera ejecución repetida
                await db.Database.MigrateAsync();

                // Comprobar que las tablas e índices siguen intactos
                var pendingCount = (await db.Database.GetPendingMigrationsAsync()).Count();
                Assert.Equal(0, pendingCount);
            }
            finally
            {
                await DropTestSchemaAsync(baseConn, schema);
            }
        }

        // =========================================================================
        // GRUPO 2: PRUEBAS DE LIMPIEZA DE FIXTURES AISLADAS
        // =========================================================================

        [SkippableFact]
        public async Task FixtureCleanup_WhenSecondFixtureCreationFails_FirstFixtureIsCleanedUpSafely()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            PostgresIsolatedScenarioFixture? fixture1 = null;
            PostgresIsolatedScenarioFixture? fixture2 = null;
            string? schema1 = null;

            try
            {
                fixture1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
                Assert.NotNull(fixture1);
                schema1 = fixture1.SchemaName;

                // Forzar fallo en la creación de fixture2 pasando cadena de conexión inválida
                var invalidConn = "Host=localhost;Port=5432;Database=non_existent_db_12345;Username=postgres;Password=invalid";
                var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                    PostgresIsolatedScenarioFixture.CreateAsync(_output, explicitConnectionString: invalidConn));
                Assert.NotNull(ex);
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, fixture1, fixture2);
            }

            // Verificar que el esquema 1 fue completamente eliminado tras el fallo de creación del 2
            await using var conn = new NpgsqlConnection(baseConn);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname = @name;";
            cmd.Parameters.AddWithValue("@name", schema1!);
            var count = (long?)await cmd.ExecuteScalarAsync();
            Assert.Equal(0, count);
        }

        [SkippableFact]
        public async Task FixtureCleanup_WhenTestThrowsDuringSeedingOrAssertions_AllFixturesAreCleanedUpSafely()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            PostgresIsolatedScenarioFixture? f1 = null;
            PostgresIsolatedScenarioFixture? f2 = null;
            string? schema1 = null;
            string? schema2 = null;
            Exception? thrownTestEx = null;

            try
            {
                f1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
                Assert.NotNull(f1);
                schema1 = f1.SchemaName;

                f2 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
                Assert.NotNull(f2);
                schema2 = f2.SchemaName;

                // Simular fallo durante aserción o siembra
                throw new InvalidOperationException("Fallo intencional durante aserción de negocio");
            }
            catch (Exception ex)
            {
                thrownTestEx = ex;
            }
            finally
            {
                await PostgresIsolatedScenarioFixture.DisposeAllAsync(thrownTestEx, f1, f2);
            }

            Assert.NotNull(thrownTestEx);
            Assert.Equal("Fallo intencional durante aserción de negocio", thrownTestEx.Message);

            // Comprobar que AMBOS esquemas fueron eliminados de PostgreSQL
            await using var conn = new NpgsqlConnection(baseConn);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname IN (@s1, @s2);";
            cmd.Parameters.AddWithValue("@s1", schema1!);
            cmd.Parameters.AddWithValue("@s2", schema2!);
            var count = (long?)await cmd.ExecuteScalarAsync();
            Assert.Equal(0, count);
        }

        [SkippableFact]
        public async Task FixtureCleanup_WhenOneFixtureFailsToDispose_OtherFixtureIsStillCleanedUp()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            PostgresIsolatedScenarioFixture? f1 = null;
            PostgresIsolatedScenarioFixture? f2 = null;
            string? schema1 = null;
            string? schema2 = null;

            f1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            Assert.NotNull(f1);
            schema1 = f1.SchemaName;

            f2 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            Assert.NotNull(f2);
            schema2 = f2.SchemaName;

            try
            {
                // Simular alteración del comentario de propiedad en f1 para que f1.DisposeAsync falle por verificación de seguridad
                await using (var conn = new NpgsqlConnection(baseConn))
                {
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $@"COMMENT ON SCHEMA ""{schema1}"" IS 'tampered_metadata';";
                    await cmd.ExecuteNonQueryAsync();
                }

                // Llamar a DisposeAllAsync. f1 fallará, pero f2 DEBE ser limpiado de todas formas
                var cleanupAggregateEx = await Assert.ThrowsAsync<AggregateException>(() =>
                    PostgresIsolatedScenarioFixture.DisposeAllAsync(null, f1, f2));

                Assert.Contains(cleanupAggregateEx.InnerExceptions, e => e.Message.Contains("No se pudo demostrar la propiedad"));

                // Verificar que f2 fue eliminado a pesar del fallo en f1
                await using (var conn = new NpgsqlConnection(baseConn))
                {
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname = @s2;";
                    cmd.Parameters.AddWithValue("@s2", schema2);
                    var count2 = (long?)await cmd.ExecuteScalarAsync();
                    Assert.Equal(0, count2);
                }
            }
            finally
            {
                // Limpiar f1 manualmente tras la prueba garantizando que no queden residuos
                await using var conn = new NpgsqlConnection(baseConn);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"DROP SCHEMA IF EXISTS ""{schema1}"" CASCADE; DROP SCHEMA IF EXISTS ""{schema2}"" CASCADE;";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        [SkippableFact]
        public async Task FixtureCleanup_PreservesOriginalTestException_AndCleanupExceptionTogether()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var f = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            Assert.NotNull(f);
            var schema = f.SchemaName;

            try
            {
                // Alterar metadata para forzar fallo de limpieza
                await using (var conn = new NpgsqlConnection(baseConn))
                {
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $@"COMMENT ON SCHEMA ""{schema}"" IS 'tampered';";
                    await cmd.ExecuteNonQueryAsync();
                }

                var originalTestEx = new InvalidOperationException("Excepción original de negocio en prueba");

                var aggEx = await Assert.ThrowsAsync<AggregateException>(() =>
                    PostgresIsolatedScenarioFixture.DisposeAllAsync(originalTestEx, f));

                // Verificar que preserva AMBAS excepciones: la original de la prueba y la de limpieza
                Assert.Contains(aggEx.InnerExceptions, e => e == originalTestEx);
                Assert.Contains(aggEx.InnerExceptions, e => e.Message.Contains("No se pudo demostrar la propiedad"));
            }
            finally
            {
                // Limpieza manual final garantizada
                await using var conn = new NpgsqlConnection(baseConn);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"DROP SCHEMA IF EXISTS ""{schema}"" CASCADE;";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        [SkippableFact]
        public async Task FixtureCleanup_PreservesForeignOrUnrelatedSchema_WithoutTouchingIt()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var foreignSchema = $"foreign_schema_{Guid.NewGuid():N}";
            await using (var conn = new NpgsqlConnection(baseConn))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    CREATE SCHEMA ""{foreignSchema}"";
                    CREATE TABLE ""{foreignSchema}"".""ForeignTable"" (id int);
                ";
                await cmd.ExecuteNonQueryAsync();
            }

            try
            {
                var f = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
                Assert.NotNull(f);
                await f.DisposeAsync();

                // Comprobar que foreignSchema sigue intacto
                await using var conn = new NpgsqlConnection(baseConn);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM pg_tables WHERE schemaname = @fs;";
                cmd.Parameters.AddWithValue("@fs", foreignSchema);
                var tableCount = (long?)await cmd.ExecuteScalarAsync();
                Assert.Equal(1, tableCount);
            }
            finally
            {
                await using var conn = new NpgsqlConnection(baseConn);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"DROP SCHEMA IF EXISTS ""{foreignSchema}"" CASCADE;";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        [SkippableFact]
        public async Task FixtureCleanup_LeavesZeroResidualSchemas_WhenCleanupCompletesSuccessfully()
        {
            var baseConn = GetConn();
            if (string.IsNullOrWhiteSpace(baseConn))
            {
                Skip.If(true, "PostgreSQL no configurado.");
                return;
            }

            var f1 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            var f2 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);
            var f3 = await PostgresIsolatedScenarioFixture.CreateAsync(_output);

            await PostgresIsolatedScenarioFixture.DisposeAllAsync(null, f1, f2, f3);

            // Verificar que no queda ningún esquema de aislamiento residual
            await using var conn = new NpgsqlConnection(baseConn);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM pg_namespace WHERE nspname LIKE 'sigetour_iso_%';";
            var residualCount = (long?)await cmd.ExecuteScalarAsync();
            Assert.Equal(0, residualCount);
        }
    }
}
